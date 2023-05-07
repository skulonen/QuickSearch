using ArcGIS.Core.Data;
using ArcGIS.Desktop.Editing.Attributes;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Controls;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

using MessageBox = ArcGIS.Desktop.Framework.Dialogs.MessageBox;

namespace QuickSearch;

public partial class QuickSearchWindow : ProWindow, INotifyPropertyChanged
{
	// Initialization

	private QuickSearchWindow()
	{
		InitializeComponent();

		DataContext = this;
		Owner = Application.Current.MainWindow;

		BindingOperations.EnableCollectionSynchronization(SearchResults, SearchResults);

		Closed += (_, _) =>
		{
			if (this == _instance)
			{
				_instance = null;
			}
		};
	}

	MapView _mapView;

	static QuickSearchWindow _instance;

	public static void Open(MapView mapView)
	{
		var instance = _instance ??= new();
		instance.Show();
		instance.Initialize(mapView);
	}

	// INotifyPropertyChanged

	public event PropertyChangedEventHandler PropertyChanged;

	void NotifyPropertyChanged(string property)
	{
		PropertyChanged?.Invoke(this, new(property));
	}

	void SetProperty<T>(T value, ref T field, [CallerMemberName] string property = null)
	{
		field = value;
		NotifyPropertyChanged(property);
	}

	// Observable properties

	MapMember _selectedLayer;
	public MapMember SelectedLayer
	{
		get => _selectedLayer;
		set
		{
			SetProperty(value, ref _selectedLayer);
			NotifyPropertyChanged(nameof(CanSelectField));

			if (value is null)
			{
				Fields = null;
			}
			else
			{
				_ = QueuedTask.Run(() => Fields = GetSearchableFields(value));
			}
		}
	}

	IEnumerable<MapMember> _layers;
	public IEnumerable<MapMember> Layers
	{
		get => _layers;
		set => SetProperty(value, ref _layers);
	}

	FieldDescription _selectedField;
	public FieldDescription SelectedField
	{
		get => _selectedField;
		set => SetProperty(value, ref _selectedField);
	}

	IEnumerable<FieldDescription> _fields;
	public IEnumerable<FieldDescription> Fields
	{
		get => _fields;
		set => SetProperty(value, ref _fields);
	}

	bool _searching;
	public bool Searching
	{
		get => _searching;
		set
		{
			SetProperty(value, ref _searching);
			NotifyPropertyChanged(nameof(CanSearch));
		}
	}

	string _searchTerm;
	public string SearchTerm
	{
		get => _searchTerm;
		set
		{
			SetProperty(value, ref _searchTerm);
			NotifyPropertyChanged(nameof(CanSearch));
		}
	}

	bool _exactSearch = true;
	public bool ExactSearch
	{
		get => _exactSearch;
		set => SetProperty(value, ref _exactSearch);
	}

	// Constant collections

	public ObservableCollection<SearchResult> SearchResults { get; } = new();

	// Calculated properties

	public bool CanSearch => !Searching && SearchTerm?.Length > 0;
	public bool CanSelectField => SelectedLayer != null;
	public bool HasResults => SearchResults?.Count > 0;

	// Commands

	RelayCommand _searchCommand;
	public ICommand SearchCommand => _searchCommand ??= new(Search, () => CanSearch);

	RelayCommand _zoomToAllResultsCommand;
	public ICommand ZoomToAllResultsCommand => _zoomToAllResultsCommand ??= new(ZoomToAllResults, () => HasResults);

	RelayCommand _selectAllResultsCommand;
	public ICommand SelectAllResultsCommand => _selectAllResultsCommand ??= new(SelectAllResults, () => HasResults);

	RelayCommand _filterAllResultsCommand;
	public ICommand FilterAllResultsCommand => _filterAllResultsCommand ??= new(FilterAllResults, () => HasResults);

	RelayCommand _zoomToResultCommand;
	public ICommand ZoomToResultCommand => _zoomToResultCommand ??= new(obj => ZoomToResult((SearchResult)obj), () => true);

	RelayCommand _selectResultCommand;
	public ICommand SelectResultCommand => _selectResultCommand ??= new(obj => SelectResult((SearchResult)obj), () => true);

	RelayCommand _filterResultCommand;
	public ICommand FilterResultCommand => _filterResultCommand ??= new(obj => FilterResult((SearchResult)obj), () => true);

	RelayCommand _showResultAttributesCommand;
	public ICommand ShowResultAttributesCommand => _showResultAttributesCommand ??= new(obj => ShowResultAttributes((SearchResult)obj), () => true);

	// Business logic entry points (must store state locally, handle errors and switch threads)

	void Initialize(MapView mapView)
	{
		try
		{
			Layers = GetSearchableLayers(mapView.Map).ToArray();
			SearchResults.Clear();

			_mapView = mapView;
		}
		catch (Exception e)
		{
			ShowErrorMessage(e);
			Close();
		}
	}

	async void Search()
	{
		try
		{
			Searching = true;
			SearchResults.Clear();
			var (layer, allLayers, field, searchTerm, exactSearch) = (SelectedLayer, Layers, SelectedField, SearchTerm, ExactSearch);

			// TODO: need to find a way to do this in parallel in case the map has a lot of layers
			// TODO: canceling
			await QueuedTask.Run(() =>
			{
				if (layer is not null)
				{
					var fields = field switch
					{
						null => GetSearchableFields(layer),
						_ => new[] { field }
					};

					var results = GetLayerResults(layer, fields, exactSearch, searchTerm);
					SearchResults.AddRange(results);
				}
				else
				{
					foreach (var layer in allLayers)
					{
						var fields = GetSearchableFields(layer);

						var results = GetLayerResults(layer, fields, exactSearch, searchTerm);
						SearchResults.AddRange(results);
					}
				}
			});
			SearchResultsListBox.Focus();
		}
		catch (Exception e)
		{
			ShowErrorMessage(e);
		}
		finally
		{
			Searching = false;
		}
	}

	async void ZoomToAllResults()
	{
		try
		{
			var results = SearchResults;
			await QueuedTask.Run(() =>
			{
				var dictionary = results
					.GroupBy(result => result.Layer)
					.Where(group => group.Key is BasicFeatureLayer)
					.ToDictionary(
						group => group.Key,
						group => group.Select(result => result.ObjectId).ToArray());

				var selectionSet = SelectionSet.FromDictionary(dictionary);
				_mapView.ZoomTo(selectionSet);
				_mapView.FlashFeature(selectionSet);
			});
		}
		catch (Exception e)
		{
			ShowErrorMessage(e);
		}
	}

	async void SelectAllResults()
	{
		try
		{
			var results = SearchResults;
			await QueuedTask.Run(() =>
			{
				foreach (var group in results.GroupBy(result => result.Layer))
				{
					var objectIds = group.Select(result => result.ObjectId).ToArray();
					((IDisplayTable)group.Key).Select(new() { ObjectIDs = objectIds });
				}
			});
		}
		catch (Exception e)
		{
			ShowErrorMessage(e);
		}
	}

	async void FilterAllResults()
	{
		try
		{
			var results = SearchResults;
			await QueuedTask.Run(() =>
			{
				foreach (var group in results.GroupBy(result => result.Layer))
				{
					var objectIds = group.Select(result => result.ObjectId);
					var objectIdField = ((IDisplayTable)group.Key).GetTable().GetDefinition().GetObjectIDField();
					((ITableDefinitionQueries)group.Key).SetDefinitionQuery($"{objectIdField} IN ({string.Join(',', objectIds)})");
				}
			});
		}
		catch (Exception e)
		{
			ShowErrorMessage(e);
		}
	}

	async void ZoomToResult(SearchResult result)
	{
		try
		{
			if (result.Layer is BasicFeatureLayer layer)
			{
				await QueuedTask.Run(() =>
				{
					_mapView.ZoomTo(layer, result.ObjectId);
					_mapView.FlashFeature(layer, result.ObjectId);
				});
			}
		}
		catch (Exception e)
		{
			ShowErrorMessage(e);
		}
	}

	async void SelectResult(SearchResult result)
	{
		try
		{
			await QueuedTask.Run(() =>
			{
				((IDisplayTable)result.Layer).Select(new() { ObjectIDs = new[] { result.ObjectId } });
			});
		}
		catch (Exception e)
		{
			ShowErrorMessage(e);
		}
	}

	async void FilterResult(SearchResult result)
	{
		try
		{
			await QueuedTask.Run(() =>
			{
				var objectIdField = ((IDisplayTable)result.Layer).GetTable().GetDefinition().GetObjectIDField();
				((ITableDefinitionQueries)result.Layer).SetDefinitionQuery($"{objectIdField} = {result.ObjectId}");
			});
		}
		catch (Exception e)
		{
			ShowErrorMessage(e);
		}
	}

	async void ShowResultAttributes(SearchResult result)
	{
		try
		{
			var inspector = new Inspector { AllowEditing = false };
			await QueuedTask.Run(() => inspector.Load(result.Layer, result.ObjectId));
			var (embeddableControl, inspectorView) = inspector.CreateEmbeddableControl();
			await embeddableControl.OpenAsync();
			var inspectorWindow = new ProWindow
			{
				Width = 400,
				Height = 400,
				Content = new DockPanel { Children = { inspectorView } },
				Padding = new(10),
				Title = $"{result.Layer.Name} {result.ObjectId}",
				Owner = Application.Current.MainWindow,
				ShowInTaskbar = false,
				WindowStartupLocation = WindowStartupLocation.CenterOwner,
				SaveWindowPosition = false
			};
			inspectorWindow.Show();
		}
		catch (Exception e)
		{
			ShowErrorMessage(e);
		}
	}

	// Business logic helpers (must not access state directly)

	static IEnumerable<MapMember> GetSearchableLayers(Map map)
	{
		IEnumerable<MapMember> EnumerateLayers(IEnumerable<Layer> layers)
		{
			foreach (var layer in layers)
			{
				if (layer is ILayerContainer container)
				{
					foreach (var subLayer in EnumerateLayers(container.Layers))
					{
						yield return subLayer;
					}
				}
				else if (layer is IDisplayTable)
				{
					yield return layer;
				}
			}
		}

		foreach (var layer in EnumerateLayers(map.Layers))
		{
			yield return layer;
		}
		foreach (var table in map.StandaloneTables)
		{
			yield return table;
		}
	}

	static IEnumerable<FieldDescription> GetSearchableFields(MapMember layer)
	{
		return ((IDisplayTable)layer).GetFieldDescriptions()
			.Where(field => field.Type is
				FieldType.Double
				or FieldType.GlobalID
				or FieldType.GUID
				or FieldType.Integer
				or FieldType.OID
				or FieldType.Single
				or FieldType.SmallInteger
				or FieldType.String);
	}

	static IEnumerable<SearchResult> GetLayerResults(MapMember layer, IEnumerable<FieldDescription> fields, bool exact, string searchTerm)
	{
		var searchConditions = fields.Select(field => GetSearchCondition(field, exact, searchTerm));
		var whereClause = string.Join(" OR ", searchConditions);

		var objectIds = new List<long>();
		using var cursor = ((IDisplayTable)layer).Search(new() { WhereClause = whereClause });
		while (cursor.MoveNext())
		{
			using var row = cursor.Current;
			objectIds.Add(row.GetObjectID());
		}

		var displayNames = ((IDisplayTable)layer).GetDisplayExpressions(objectIds);

		return objectIds.Zip(displayNames, (objectId, displayName) => new SearchResult
		{
			Layer = layer,
			ObjectId = objectId,
			DisplayName = displayName
		});
	}

	static string GetSearchCondition(FieldDescription field, bool exact, string searchTerm)
	{
		var fieldSql = field.Type switch
		{
			FieldType.String => field.Name,
			// 100 characters should be enough to hold any number or GUID
			_ => $"CAST({field.Name} AS VARCHAR(100))"
		};

		var escapedSearchTerm = searchTerm.Replace("'", "''");
		var formattedSearchTerm = field.Type switch
		{
			FieldType.Double or FieldType.Single => escapedSearchTerm.Replace(',', '.'),
			_ => escapedSearchTerm
		};

		var searchCondition = exact switch
		{
			true => $"({fieldSql} = '{formattedSearchTerm}')",
			false => $"({fieldSql} LIKE '%{formattedSearchTerm}%')"
		};
		return searchCondition;
	}

	static void ShowErrorMessage(Exception e)
	{
		MessageBox.Show(e.ToString(), null, MessageBoxButton.OK, MessageBoxImage.Error);
	}
}
