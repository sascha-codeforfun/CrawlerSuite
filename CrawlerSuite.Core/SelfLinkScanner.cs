namespace CrawlerSuite.Core
{
	using HtmlAgilityPack;
	using System.Collections.Concurrent;
	using System.Text;
	using System.Web;

	public static class SelfLinkScanner
	{
		public static void FindSelfLinks(
			string directoryToScanPath,
			string scanningResultsCsvBasePath,
			List<string> queryStringsToIgnoreForSelfLinkDetermination,
			string filePattern,
			int maxDegreeOfParallelism = 0,
			int contextRadiusChars = 120,
			int contextSnippetLength = 240,
			Func<string, IReadOnlyList<string>>? templatePartsOf = null,
			string? forwardingCsvBasePath = null)
		{
			// forwardingCsvBasePath: where links to the site's own server that hand it a full address
			// in their query are written (log 40). They are no self-links — the server is to forward the
			// visitor elsewhere — so they leave log 09 whether or not log 40 is written. A template's
			// such link on a reference page is reported there once, like the template itself.
			// templatePartsOf: for a page, the XPaths of its template parts that the prune kept —
			// on a reference page the prune keeps the template so it is checked once there; a link
			// inside it (the logo on the home page) is the template's, not the editor's.
			var htmlFiles = CrawlerSuite.Core.Host.FolderFiles.Sorted(directoryToScanPath, filePattern, SearchOption.AllDirectories);
			var results = new ConcurrentQueue<string?[]>();
			var forwarding = new ConcurrentQueue<string?[]>();

			var ignoredQueryKeys = queryStringsToIgnoreForSelfLinkDetermination is { Count: > 0 }
				? new HashSet<string>(queryStringsToIgnoreForSelfLinkDetermination, StringComparer.OrdinalIgnoreCase)
				: null;

			int resolvedDop = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
			var maxDop = Math.Max(1, resolvedDop - 1);
			var po = new ParallelOptions { MaxDegreeOfParallelism = maxDop };

			RunUi.StepTotal(htmlFiles.Length);
			Parallel.ForEach(htmlFiles, po, diskFile =>
			{
				RunUi.StepAdvance();
				try
				{
					var fileName = Path.GetFileName(diskFile);
					var fileUrl = CrawlIndex.LookUpUrlForFile(fileName);

					if (string.IsNullOrWhiteSpace(fileUrl))
					{
						return;
					}

					Uri baseUri;
					try
					{
						baseUri = new Uri(fileUrl);
					}
					catch
					{
						return;
					}

					string fileContent;
					try
					{
						fileContent = CrawlerSuite.Core.Host.WorkFile.ReadAllText(diskFile, Encoding.UTF8);
					}
					catch
					{
						return;
					}

					var doc = CrawlerSuite.Core.Html.HtmlDocuments.Create();
					doc.LoadHtml(fileContent);

					var anchorNodes = doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>();

					// The template parts the prune left in (a reference page), as the prune would remove them.
					HashSet<HtmlNode>? template = null;
					foreach (var xpath in templatePartsOf?.Invoke(fileUrl) ?? [])
					{
						foreach (var node in doc.DocumentNode.SelectNodes(xpath) ?? Enumerable.Empty<HtmlNode>())
						{
							(template ??= []).Add(node);
						}
					}

					bool InTemplate(HtmlNode node)
					{
						for (var n = node; n is not null && template is not null; n = n.ParentNode)
						{
							if (template.Contains(n))
							{
								return true;
							}
						}

						return false;
					}

					var baseOrigin = baseUri.GetLeftPart(UriPartial.Authority);

					foreach (var a in anchorNodes)
					{
						bool inTemplate = template is not null && InTemplate(a);

						var hrefValue = HtmlEntity.DeEntitize(a.GetAttributeValue("href", string.Empty));
						if (string.IsNullOrWhiteSpace(hrefValue))
						{
							continue;
						}

						var hrefNormalized = hrefValue.Trim();

						// Resolve to absolute first, then gate on the resolved origin.
						// Absolute hrefs are taken as-is; rooted ("/x"), document-relative
						// ("x.html", "./x.html"), and query-only forms resolve against the
						// page's own URL. Gating on the resolved authority keeps same-site
						// self-links in every href form while off-site links and non-http
						// schemes (mailto:/tel:/javascript:) resolve to a different or empty
						// authority and drop out here.
						Uri resolvedHrefUri;
						try
						{
							if (Uri.TryCreate(hrefNormalized, UriKind.Absolute, out var abs))
							{
								resolvedHrefUri = abs;
							}
							else
							{
								resolvedHrefUri = new Uri(baseUri, hrefNormalized);
							}
						}
						catch
						{
							continue;
						}

						var resolvedOrigin = resolvedHrefUri.GetLeftPart(UriPartial.Authority);
						if (!string.Equals(baseOrigin, resolvedOrigin, StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}

						// A link that hands the site's server an address to forward to: log 40, never log 09.
						// Read from the saved page only — the app never requests it.
						var targets = Links.ForwardingLinks.In(resolvedHrefUri);
						if (targets.Count > 0)
						{
							foreach (var target in targets)
							{
								forwarding.Enqueue(
								[
									fileUrl, hrefValue, target.Parameter, target.Address, target.Host,
									target.External ? Links.ForwardingLinks.ExternalScope : Links.ForwardingLinks.SameHostScope,
								]);
							}

							continue;
						}

						// The template's own links on a reference page are no editor's self-links.
						if (inTemplate)
						{
							continue;
						}

						if (ignoredQueryKeys is not null && !string.IsNullOrEmpty(resolvedHrefUri.Query))
						{
							var q = HttpUtility.ParseQueryString(resolvedHrefUri.Query);
							var keys = q.AllKeys;
							if (keys is not null)
							{
								bool hasIgnored = false;
								foreach (var k in keys)
								{
									if (k is null)
									{
										continue;
									}

									if (ignoredQueryKeys.Contains(k))
									{
										hasIgnored = true;
										break;
									}
								}
								if (hasIgnored)
								{
									continue;
								}
							}
						}

						var hrefPath = resolvedHrefUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
						string filePath;
						try
						{
							filePath = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
						}
						catch
						{
							filePath = fileUrl.TrimEnd('/');
						}

						if (string.Equals(hrefPath, filePath, StringComparison.OrdinalIgnoreCase))
						{
							if (hrefNormalized.Contains('#'))
							{
								continue;
							}

							var snippet = ExtractHtmlContextSnippet(fileContent, a, contextRadiusChars, contextSnippetLength);

							// [KEEP] Composed via IssueLogWriter, never a local
							// escape helper. Each field
							// is sanitized for CR / LF / control chars / delimiter.
							results.Enqueue(new string?[]
							{
								fileName, fileUrl, hrefValue, snippet ?? string.Empty
							});
						}
					}
				}
				catch
				{
					// swallow per-file exceptions
				}
			});

			// Drain queue and sort deterministically before write. The
			// Parallel.ForEach above enqueues in completion order, which is
			// CPU-scheduling-dependent — same input could produce different
			// byte-level output across runs. Sort by the assembled fields
			// (filename first) for a stable, alphabetic ordering that is also
			// useful to operators reading the log (related files cluster).
			var dataRows = new List<string?[]>(results.Count);
			while (results.TryDequeue(out var row))
			{
				dataRows.Add(row);
			}
			dataRows.Sort(static (a, b) =>
			{
				int n = Math.Min(a.Length, b.Length);
				for (int i = 0; i < n; i++)
				{
					int c = string.CompareOrdinal(a[i] ?? string.Empty, b[i] ?? string.Empty);
					if (c != 0)
					{
						return c;
					}
				}
				return a.Length - b.Length;
			});

			// Dual-locale CSV pair (BOM, RFC-4180 quoted) via IssueLogWriter.WriteCsvPair:
			// ContextSnippet (HTML) carries a delimiter verbatim (quoted) instead of
			// having it stripped.
			var records = new List<string?[]>(dataRows.Count + 1)
			{
				new string?[] { "File", "FileUrl", "LinkFound", "ContextSnippet" }
			};
			records.AddRange(dataRows);
			IssueLogWriter.WriteCsvPair(scanningResultsCsvBasePath, records);

			if (forwardingCsvBasePath is not null)
			{
				// External first, then by page, link and parameter: a stable order across runs.
				var rows = forwarding.Distinct(RowComparer.Instance)
					.OrderBy(r => r[5] == Links.ForwardingLinks.ExternalScope ? 0 : 1)
					.ThenBy(r => r[0], StringComparer.Ordinal).ThenBy(r => r[1], StringComparer.Ordinal).ThenBy(r => r[2], StringComparer.Ordinal)
					.ThenBy(r => r[3], StringComparer.Ordinal)
					.ToList();
				var forwardingRecords = new List<string?[]>(rows.Count + 1)
				{
					new string?[] { "Page", "Link", "Parameter", "Target", "TargetHost", "Scope" },
				};
				forwardingRecords.AddRange(rows);
				IssueLogWriter.WriteCsvPair(forwardingCsvBasePath, forwardingRecords);
			}
		}

		/// <summary>Rows equal field by field: a link written twice on a page is one row.</summary>
		private sealed class RowComparer : IEqualityComparer<string?[]>
		{
			public static readonly RowComparer Instance = new();

			public bool Equals(string?[]? x, string?[]? y) => x is not null && y is not null && x.SequenceEqual(y, StringComparer.Ordinal);

			public int GetHashCode(string?[] row) => row.Aggregate(17, (h, f) => h * 31 + StringComparer.Ordinal.GetHashCode(f ?? string.Empty));
		}

		public static string ExtractHtmlContextSnippet(string fileContent, HtmlNode aNode, int contextRadiusChars, int maxSnippetLength)
		{
			if (string.IsNullOrEmpty(fileContent) || aNode == null)
			{
				return string.Empty;
			}

			string needle = aNode.OuterHtml ?? aNode.InnerHtml ?? aNode.InnerText ?? string.Empty;
			if (string.IsNullOrEmpty(needle))
			{
				return string.Empty;
			}

			int idx = fileContent.IndexOf(needle, StringComparison.Ordinal);
			if (idx < 0)
			{
				idx = fileContent.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
			}

			if (idx < 0)
			{
				string textOnly = aNode.InnerText ?? string.Empty;
				if (!string.IsNullOrEmpty(textOnly))
				{
					idx = fileContent.IndexOf(textOnly, StringComparison.Ordinal);
					if (idx < 0)
					{
						idx = fileContent.IndexOf(textOnly, StringComparison.OrdinalIgnoreCase);
					}
				}
			}

			if (idx < 0)
			{
				return fileContent.Length <= maxSnippetLength ? fileContent : fileContent.Substring(0, maxSnippetLength);
			}

			int start = Math.Max(0, idx - contextRadiusChars);
			int end = Math.Min(fileContent.Length, idx + needle.Length + contextRadiusChars);
			int length = end - start;

			if (length > maxSnippetLength)
			{
				int center = idx + needle.Length / 2;
				start = Math.Max(0, center - maxSnippetLength / 2);
				if (start + maxSnippetLength > fileContent.Length)
				{
					start = Math.Max(0, fileContent.Length - maxSnippetLength);
				}

				length = Math.Min(maxSnippetLength, fileContent.Length - start);
			}
			else if (length < maxSnippetLength)
			{
				int need = maxSnippetLength - length;
				end = Math.Min(fileContent.Length, end + need);
				length = end - start;
				if (length < maxSnippetLength)
				{
					int more = Math.Min(start, maxSnippetLength - length);
					start -= more;
				}
			}

			return fileContent.Substring(start, Math.Min(maxSnippetLength, end - start));
		}
	}
}
