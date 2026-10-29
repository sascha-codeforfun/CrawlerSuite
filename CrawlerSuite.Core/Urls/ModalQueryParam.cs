namespace CrawlerSuite.Core.Urls
{
	using System;
	using System.Text.Json;
	using System.Text.Json.Serialization;

	/// <summary>
	/// One entry in <see cref="Config.ModalQueryParameters"/>: a query-parameter name
	/// whose value carries a URL-encoded target URL (modal/overlay/popup carrier),
	/// plus the optional page suffix to append when the decoded target is
	/// extension-less.
	///
	/// <para>
	/// BACKGROUND — why <see cref="AppendSuffix"/> exists. Some CMSes reference modal
	/// content by an <em>extension-less</em> resource path encoded into a carrier
	/// page's query string, e.g. <c>carrier.html?modal=%2Fpath%2Fto%2Ffragment</c>.
	/// The real, fetchable page is that decoded path plus the site's page extension
	/// (e.g. <c>.html</c>); the bare extension-less form may not resolve on its own
	/// (a redirect-to-directory that then fails on a direct GET). <see cref="AppendSuffix"/>
	/// restores that extension — per-parameter and from config, so the engine stays
	/// generic (no hard-coded parameter name or extension).
	/// </para>
	///
	/// <para>
	/// TWO SHAPES. The config array accepts BOTH shapes per element:
	/// <list type="bullet">
	///   <item><description>
	///     a bare string — <c>"popup"</c> — the short shape. Binds to
	///     <see cref="AppendSuffix"/> = <c>""</c>: extract, decode, strip the inner
	///     query, NO suffix.
	///   </description></item>
	///   <item><description>
	///     an object — <c>{ "Param": "popup", "AppendSuffix": ".html" }</c> — the
	///     shape that opts into the suffix. <c>Param</c> is required; a missing
	///     <c>Param</c> on an object element fails loudly at parse, so a config of
	///     the wrong shape can never silently bind to a default.
	///   </description></item>
	/// </list>
	/// Mixed arrays are allowed: <c>[ "overlay", { "Param": "popup", "AppendSuffix": ".html" } ]</c>.
	/// </para>
	/// </summary>
	[JsonConverter(typeof(ModalQueryParamConverter))]
	public sealed record ModalQueryParam(string Param, string AppendSuffix = "")
	{
		/// <summary>
		/// Applies <see cref="AppendSuffix"/> to a decoded modal target, idempotently.
		/// No-op when the suffix is empty (bare string entries), when the target
		/// already carries an extension, when it ends in <c>/</c> (a directory form),
		/// or when it already ends with the suffix (prevents the <c>.html.html</c>
		/// double-suffix class). Returns <paramref name="target"/> unchanged in all
		/// those cases, so a bare-string entry yields the decoded target as it is.
		/// </summary>
		public string ApplySuffix(string target)
		{
			if (string.IsNullOrEmpty(AppendSuffix) || string.IsNullOrEmpty(target))
			{
				return target;
			}

			if (target.EndsWith("/", StringComparison.Ordinal))
			{
				return target;
			}

			if (target.EndsWith(AppendSuffix, StringComparison.OrdinalIgnoreCase))
			{
				return target;
			}

			// Path.HasExtension over the path segment only — guard against a query or
			// fragment (there should be none here post-RemoveQueryString, but be safe)
			// and against a dotted directory name being mistaken for an extension is
			// out of scope; the crawler's modal targets are simple node paths.
			if (HasPathExtension(target))
			{
				return target;
			}

			return target + AppendSuffix;
		}

		private static bool HasPathExtension(string url)
		{
			// Consider only the last path segment. Avoids Path.HasExtension quirks on
			// full URLs and treats "…/foo.bar/baz" (dotted directory) correctly.
			var lastSlash = url.LastIndexOf('/');
			var lastSegment = lastSlash >= 0 ? url[(lastSlash + 1)..] : url;
			var dot = lastSegment.LastIndexOf('.');
			return dot > 0 && dot < lastSegment.Length - 1;
		}
	}

	/// <summary>
	/// System.Text.Json converter that lets each <see cref="ModalQueryParam"/> element
	/// be written as a plain string or an object. See the type doc for the two
	/// shapes.
	/// </summary>
	public sealed class ModalQueryParamConverter : JsonConverter<ModalQueryParam>
	{
		public override ModalQueryParam Read(
			ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			switch (reader.TokenType)
			{
				// Short shape: "popup"  →  suffix "".
				case JsonTokenType.String:
				{
					var name = reader.GetString();
					if (string.IsNullOrWhiteSpace(name))
					{
						throw new JsonException(
							"ModalQueryParameters: a string entry must be a non-empty parameter name.");
					}

					return new ModalQueryParam(name, string.Empty);
				}

				// Object shape: { "Param": "popup", "AppendSuffix": ".html" }
				case JsonTokenType.StartObject:
				{
					using var doc = JsonDocument.ParseValue(ref reader);
					var root = doc.RootElement;

					// Param is required — fail loud rather than bind-to-default.
					if (!TryGetPropertyCaseInsensitive(root, "Param", out var paramEl)
						|| paramEl.ValueKind != JsonValueKind.String
						|| string.IsNullOrWhiteSpace(paramEl.GetString()))
					{
						throw new JsonException(
							"ModalQueryParameters: an object entry requires a non-empty 'Param' string.");
					}

					var name = paramEl.GetString()!;

					var suffix = string.Empty;
					if (TryGetPropertyCaseInsensitive(root, "AppendSuffix", out var suffixEl)
						&& suffixEl.ValueKind == JsonValueKind.String)
					{
						suffix = suffixEl.GetString() ?? string.Empty;
					}

					return new ModalQueryParam(name, suffix);
				}

				default:
					throw new JsonException(
						"ModalQueryParameters: each entry must be a string or an object " +
						"({ \"Param\": \"…\", \"AppendSuffix\": \"…\" }).");
			}
		}

		public override void Write(
			Utf8JsonWriter writer, ModalQueryParam value, JsonSerializerOptions options)
		{
			// The shortest shape the reader accepts: a bare string when there is no
			// suffix, the object shape otherwise.
			if (string.IsNullOrEmpty(value.AppendSuffix))
			{
				writer.WriteStringValue(value.Param);
				return;
			}

			writer.WriteStartObject();
			writer.WriteString("Param", value.Param);
			writer.WriteString("AppendSuffix", value.AppendSuffix);
			writer.WriteEndObject();
		}

		private static bool TryGetPropertyCaseInsensitive(
			JsonElement obj, string name, out JsonElement value)
		{
			foreach (var prop in obj.EnumerateObject())
			{
				if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					value = prop.Value;
					return true;
				}
			}

			value = default;
			return false;
		}
	}
}
