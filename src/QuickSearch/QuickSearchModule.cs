using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Mapping;

namespace QuickSearch;

public class QuickSearchModule : Module
{
	public void OpenSearch()
	{
		if (MapView.Active is var mapView)
		{
			QuickSearchWindow.Open(mapView);
		}
	}
}
