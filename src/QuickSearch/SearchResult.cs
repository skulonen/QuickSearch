using ArcGIS.Desktop.Mapping;

namespace QuickSearch;

public class SearchResult
{
	public MapMember Layer { get; init; }
	public long ObjectId { get; init; }
	public string DisplayName { get; init; }

	public override string ToString()
	{
		return $"{Layer.Name} {DisplayName} ({ObjectId})";
	}
}
