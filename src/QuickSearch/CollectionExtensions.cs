﻿using System.Collections;
using System.Collections.Generic;

namespace QuickSearch;

public static class CollectionExtensions
{
	public static void AddRange<T>(this ICollection<T> collection, IEnumerable<T> values)
	{
		foreach (var value in values)
		{
			collection.Add(value);
		}
	}
}
