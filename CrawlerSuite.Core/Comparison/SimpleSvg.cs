namespace CrawlerSuite.Core.Comparison
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.IO;
	using System.Linq;
	using System.Text;
	using System.Text.RegularExpressions;
	using System.Xml;
	using System.Xml.Linq;

	/// <summary>
	/// A simple SVG read into shapes the app can draw without a library: paths, rectangles,
	/// circles, ellipses, lines, polylines and polygons, in groups, with fill, stroke, opacity, fill
	/// rule and transforms. Everything else — text, gradients, filters, masks, clip paths, use,
	/// embedded images, CSS — makes the whole SVG not drawn, with the reason: a preview that left
	/// parts out would look complete and mislead. Path data is rewritten into a form any path
	/// parser reads (SVG's packed arc flags and numbers like "1.15.15" taken apart).
	/// </summary>
	public static class SimpleSvg
	{
		/// <summary>A fill or stroke the SVG leaves to the surroundings (currentColor, or no fill given): drawn in the app's text colour.</summary>
		public const string Current = "current";

		/// <summary>One shape: its path (absolute and relative commands, each with its own letter), paint and the transform it is drawn with.</summary>
		/// <param name="Fill">"#AARRGGBB", a colour name, <see cref="Current"/>, or null for none.</param>
		/// <param name="Transform">The matrix a, b, c, d, e, f (SVG order).</param>
		public sealed record Shape(string Path, bool EvenOdd, string? Fill, string? Stroke, double StrokeWidth, double FillOpacity, double StrokeOpacity, double[] Transform);

		/// <summary>An SVG that can be drawn: its view box and shapes.</summary>
		public sealed record Drawing(double MinX, double MinY, double Width, double Height, IReadOnlyList<Shape> Shapes)
		{
			/// <summary>Some fill or stroke was left open and is drawn in the text colour.</summary>
			public bool UsesCurrentColour => Shapes.Any(s => s.Fill == Current || s.Stroke == Current);
		}

		private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal) { "title", "desc", "metadata" };

		private static readonly HashSet<string> Drawn = new(StringComparer.Ordinal) { "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon" };

		private static readonly Dictionary<string, string> Unsupported = new(StringComparer.Ordinal)
		{
			["text"] = "text", ["tspan"] = "text", ["textPath"] = "text",
			["linearGradient"] = "gradients", ["radialGradient"] = "gradients",
			["filter"] = "filters", ["mask"] = "masks", ["clipPath"] = "clip paths", ["pattern"] = "patterns",
			["use"] = "use", ["symbol"] = "symbols", ["image"] = "embedded images", ["style"] = "CSS",
			["foreignObject"] = "foreign content", ["svg"] = "nested SVGs", ["marker"] = "markers",
			["animate"] = "animation", ["animateTransform"] = "animation", ["set"] = "animation", ["switch"] = "switch",
		};

		/// <summary>The drawing of an SVG, or why it is not drawn.</summary>
		public static (Drawing? Drawing, string Problem) Read(string text)
		{
			XDocument doc;
			try
			{
				using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
				doc = XDocument.Load(reader);
			}
			catch (XmlException ex)
			{
				return (null, "Not drawn: the SVG does not read as XML (" + ex.Message + ")");
			}

			var root = doc.Root;
			if (root is null || root.Name.LocalName != "svg")
			{
				return (null, "Not drawn: no <svg> element.");
			}

			if (ViewBox(root) is not { } box)
			{
				return (null, "Not drawn: it gives neither a viewBox nor a width and height.");
			}

			var shapes = new List<Shape>();
			var problems = new SortedSet<string>(StringComparer.Ordinal);
			Walk(root, new Paint(Current, null, 1, false, 1, 1, 1), Identity, shapes, problems, isRoot: true);
			if (problems.Count > 0)
			{
				return (null, "Not drawn: it uses " + string.Join(", ", problems) + ", which the simple preview does not draw.");
			}

			return shapes.Count == 0 ? (null, "Not drawn: it has no shapes.") : (new Drawing(box.X, box.Y, box.W, box.H, shapes), string.Empty);
		}

		private sealed record Paint(string? Fill, string? Stroke, double StrokeWidth, bool EvenOdd, double Opacity, double FillOpacity, double StrokeOpacity);

		private static readonly double[] Identity = [1, 0, 0, 1, 0, 0];

		private static void Walk(XElement element, Paint inherited, double[] matrix, List<Shape> shapes, ISet<string> problems, bool isRoot = false)
		{
			var paint = PaintOf(element, inherited, problems);
			var own = isRoot ? matrix : Multiply(matrix, TransformOf(element.Attribute("transform")?.Value, problems));
			foreach (var child in element.Elements())
			{
				var name = child.Name.LocalName;
				if (Ignored.Contains(name))
				{
					continue;
				}

				if (name == "defs")
				{
					if (child.HasElements)
					{
						problems.Add("definitions (defs)");
					}

					continue;
				}

				if (Unsupported.TryGetValue(name, out var what))
				{
					problems.Add(what);
					continue;
				}

				if (!Drawn.Contains(name))
				{
					problems.Add("<" + name + ">");
					continue;
				}

				if (name == "g")
				{
					Walk(child, paint, own, shapes, problems);
					continue;
				}

				var shapePaint = PaintOf(child, paint, problems);
				var shapeMatrix = Multiply(own, TransformOf(child.Attribute("transform")?.Value, problems));
				var path = PathOf(child, problems);
				if (path is null || (shapePaint.Fill is null && shapePaint.Stroke is null))
				{
					continue;
				}

				shapes.Add(new Shape(path, shapePaint.EvenOdd, shapePaint.Fill, shapePaint.Stroke, shapePaint.StrokeWidth,
					shapePaint.Opacity * shapePaint.FillOpacity, shapePaint.Opacity * shapePaint.StrokeOpacity, shapeMatrix));
			}
		}

		// ── Paint ──────────────────────────────────────────────────────────────

		private static Paint PaintOf(XElement e, Paint inherited, ISet<string> problems)
		{
			var props = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var a in e.Attributes())
			{
				props[a.Name.LocalName] = a.Value.Trim();
			}

			if (props.TryGetValue("style", out var style))
			{
				foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
				{
					int colon = declaration.IndexOf(':');
					if (colon > 0)
					{
						props[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
					}
				}
			}

			string? Colour(string key, string? from)
			{
				if (!props.TryGetValue(key, out var value) || value.Length == 0 || value == "inherit")
				{
					return from;
				}

				if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
				{
					problems.Add("gradients or patterns");
					return from;
				}

				return value == "none" || value == "transparent" ? null : value == "currentColor" ? Current : Normalise(value, problems);
			}

			double Number(string key, double from) =>
				props.TryGetValue(key, out var value) && double.TryParse(value.TrimEnd('x', 'p', '%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
					? (value.EndsWith('%') ? n / 100 : n)
					: from;

			bool evenOdd = props.TryGetValue("fill-rule", out var rule) ? rule == "evenodd" : inherited.EvenOdd;
			return new Paint(Colour("fill", inherited.Fill), Colour("stroke", inherited.Stroke), Number("stroke-width", inherited.StrokeWidth), evenOdd,
				inherited.Opacity * Number("opacity", 1), Number("fill-opacity", inherited.FillOpacity), Number("stroke-opacity", inherited.StrokeOpacity));
		}

		private static readonly Regex Rgb = new(@"^rgba?\(\s*([\d.]+%?)\s*[, ]\s*([\d.]+%?)\s*[, ]\s*([\d.]+%?)\s*(?:[,/]\s*([\d.]+%?)\s*)?\)$", RegexOptions.IgnoreCase);

		/// <summary>A colour as "#AARRGGBB", or its name (lower case) for the app to look up.</summary>
		internal static string? Normalise(string value, ISet<string> problems)
		{
			var v = value.Trim();
			if (v.StartsWith('#'))
			{
				var hex = v[1..];
				if (!hex.All(Uri.IsHexDigit))
				{
					problems.Add("a colour it cannot read (" + value + ")");
					return null;
				}

				return hex.Length switch
				{
					3 => $"#FF{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}".ToUpperInvariant(),
					4 => $"#{hex[3]}{hex[3]}{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}".ToUpperInvariant(),
					6 => ("#FF" + hex).ToUpperInvariant(),
					8 => ("#" + hex[6..] + hex[..6]).ToUpperInvariant(), // SVG/CSS #RRGGBBAA → #AARRGGBB
					_ => Fail(),
				};
			}

			var m = Rgb.Match(v);
			if (m.Success)
			{
				int Channel(string s) => (int)Math.Round(Math.Clamp(s.EndsWith('%')
					? double.Parse(s[..^1], CultureInfo.InvariantCulture) * 2.55
					: double.Parse(s, CultureInfo.InvariantCulture), 0, 255));
				int alpha = m.Groups[4].Success
					? (int)Math.Round(Math.Clamp((m.Groups[4].Value.EndsWith('%') ? double.Parse(m.Groups[4].Value[..^1], CultureInfo.InvariantCulture) / 100
						: double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture)) * 255, 0, 255))
					: 255;
				return $"#{alpha:X2}{Channel(m.Groups[1].Value):X2}{Channel(m.Groups[2].Value):X2}{Channel(m.Groups[3].Value):X2}";
			}

			if (Regex.IsMatch(v, "^[A-Za-z]+$"))
			{
				return v.ToLowerInvariant();
			}

			return Fail();

			string? Fail()
			{
				problems.Add("a colour it cannot read (" + value + ")");
				return null;
			}
		}

		// ── Geometry ──────────────────────────────────────────────────────────

		private static (double X, double Y, double W, double H)? ViewBox(XElement svg)
		{
			var numbers = Numbers(svg.Attribute("viewBox")?.Value ?? string.Empty);
			if (numbers.Count == 4 && numbers[2] > 0 && numbers[3] > 0)
			{
				return (numbers[0], numbers[1], numbers[2], numbers[3]);
			}

			double? Length(string name) =>
				svg.Attribute(name)?.Value is { } s && double.TryParse(s.Trim().Replace("px", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
			return Length("width") is { } w && Length("height") is { } h ? (0, 0, w, h) : null;
		}

		private static string? PathOf(XElement e, ISet<string> problems)
		{
			double N(string name) => double.TryParse(e.Attribute(name)?.Value?.Trim().Replace("px", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
			string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
			switch (e.Name.LocalName)
			{
				case "path":
					var d = e.Attribute("d")?.Value;
					if (string.IsNullOrWhiteSpace(d))
					{
						return null;
					}

					var path = NormalisePath(d);
					if (path is null)
					{
						problems.Add("path data it cannot read");
					}

					return path;
				case "rect":
				{
					double x = N("x"), y = N("y"), w = N("width"), h = N("height");
					if (w <= 0 || h <= 0)
					{
						return null;
					}

					double rx = N("rx"), ry = N("ry");
					rx = rx <= 0 ? ry : rx;
					ry = ry <= 0 ? rx : ry;
					rx = Math.Min(rx, w / 2);
					ry = Math.Min(ry, h / 2);
					if (rx <= 0)
					{
						return $"M {F(x)} {F(y)} H {F(x + w)} V {F(y + h)} H {F(x)} Z";
					}

					return $"M {F(x + rx)} {F(y)} H {F(x + w - rx)} A {F(rx)} {F(ry)} 0 0 1 {F(x + w)} {F(y + ry)} V {F(y + h - ry)} "
						+ $"A {F(rx)} {F(ry)} 0 0 1 {F(x + w - rx)} {F(y + h)} H {F(x + rx)} A {F(rx)} {F(ry)} 0 0 1 {F(x)} {F(y + h - ry)} "
						+ $"V {F(y + ry)} A {F(rx)} {F(ry)} 0 0 1 {F(x + rx)} {F(y)} Z";
				}

				case "circle":
				case "ellipse":
				{
					double cx = N("cx"), cy = N("cy");
					double rx = e.Name.LocalName == "circle" ? N("r") : N("rx");
					double ry = e.Name.LocalName == "circle" ? N("r") : N("ry");
					if (rx <= 0 || ry <= 0)
					{
						return null;
					}

					return $"M {F(cx - rx)} {F(cy)} A {F(rx)} {F(ry)} 0 1 0 {F(cx + rx)} {F(cy)} A {F(rx)} {F(ry)} 0 1 0 {F(cx - rx)} {F(cy)} Z";
				}

				case "line":
					return $"M {F(N("x1"))} {F(N("y1"))} L {F(N("x2"))} {F(N("y2"))}";
				case "polyline":
				case "polygon":
				{
					var p = Numbers(e.Attribute("points")?.Value ?? string.Empty);
					if (p.Count < 4)
					{
						return null;
					}

					var sb = new StringBuilder($"M {F(p[0])} {F(p[1])}");
					for (int i = 2; i + 1 < p.Count; i += 2)
					{
						sb.Append($" L {F(p[i])} {F(p[i + 1])}");
					}

					return e.Name.LocalName == "polygon" ? sb.Append(" Z").ToString() : sb.ToString();
				}
			}

			return null;
		}

		private static readonly Regex NumberToken = new(@"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.Compiled);

		private static List<double> Numbers(string text) =>
			NumberToken.Each(text).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();

		private static readonly Dictionary<char, int> Arity = new()
		{
			['M'] = 2, ['L'] = 2, ['H'] = 1, ['V'] = 1, ['C'] = 6, ['S'] = 4, ['Q'] = 4, ['T'] = 2, ['A'] = 7, ['Z'] = 0,
		};

		/// <summary>
		/// SVG path data rewritten so every command carries its own letter and every number stands
		/// apart: implicit repeats spelled out (after a move, lines), packed arc flags ("011") and
		/// packed numbers ("1.15.15") taken apart. Null when the data does not read.
		/// </summary>
		public static string? NormalisePath(string d)
		{
			var output = new StringBuilder();
			int i = 0;
			char command = '\0';
			bool first = true;
			while (true)
			{
				Skip(d, ref i);
				if (i >= d.Length)
				{
					break;
				}

				char c = d[i];
				if (char.IsLetter(c))
				{
					if (!Arity.ContainsKey(char.ToUpperInvariant(c)))
					{
						return null;
					}

					command = c;
					i++;
					first = true;
					if (char.ToUpperInvariant(c) == 'Z')
					{
						output.Append(" Z");
						continue;
					}
				}
				else if (command == '\0' || char.ToUpperInvariant(command) == 'Z')
				{
					return null;
				}
				else if (!first && char.ToUpperInvariant(command) == 'M')
				{
					command = command == 'M' ? 'L' : 'l'; // further pairs after a move are lines
				}

				char upper = char.ToUpperInvariant(command);
				var values = new List<string>(Arity[upper]);
				for (int k = 0; k < Arity[upper]; k++)
				{
					Skip(d, ref i);
					if (upper == 'A' && (k == 3 || k == 4))
					{
						if (i >= d.Length || (d[i] != '0' && d[i] != '1'))
						{
							return null;
						}

						values.Add(d[i].ToString());
						i++;
						continue;
					}

					var m = NumberToken.Match(d, i);
					if (!m.Success || m.Index != i)
					{
						return null;
					}

					values.Add(double.Parse(m.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
					i += m.Length;
				}

				output.Append(' ').Append(command).Append(' ').Append(string.Join(' ', values));
				first = false;
			}

			return output.Length == 0 ? null : output.ToString().Trim();
		}

		private static void Skip(string d, ref int i)
		{
			while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ','))
			{
				i++;
			}
		}

		// ── Transforms ────────────────────────────────────────────────────────

		private static readonly Regex TransformPart = new(@"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)", RegexOptions.Compiled);

		private static double[] TransformOf(string? text, ISet<string> problems)
		{
			var result = Identity;
			if (string.IsNullOrWhiteSpace(text))
			{
				return result;
			}

			var rest = TransformPart.Replace(text, string.Empty).Trim().Trim(',').Trim();
			if (rest.Length > 0)
			{
				problems.Add("a transform it cannot read");
				return result;
			}

			foreach (Match m in TransformPart.Each(text))
			{
				var n = Numbers(m.Groups[2].Value);
				double[] t = m.Groups[1].Value switch
				{
					"matrix" when n.Count == 6 => [n[0], n[1], n[2], n[3], n[4], n[5]],
					"translate" when n.Count >= 1 => [1, 0, 0, 1, n[0], n.Count > 1 ? n[1] : 0],
					"scale" when n.Count >= 1 => [n[0], 0, 0, n.Count > 1 ? n[1] : n[0], 0, 0],
					"rotate" when n.Count >= 1 => Rotate(n[0], n.Count >= 3 ? n[1] : 0, n.Count >= 3 ? n[2] : 0),
					"skewX" when n.Count == 1 => [1, 0, Math.Tan(n[0] * Math.PI / 180), 1, 0, 0],
					"skewY" when n.Count == 1 => [1, Math.Tan(n[0] * Math.PI / 180), 0, 1, 0, 0],
					_ => Bad(),
				};
				result = Multiply(result, t);
			}

			return result;

			double[] Bad()
			{
				problems.Add("a transform it cannot read");
				return Identity;
			}
		}

		private static double[] Rotate(double degrees, double cx, double cy)
		{
			double r = degrees * Math.PI / 180, cos = Math.Cos(r), sin = Math.Sin(r);
			var turn = new[] { cos, sin, -sin, cos, 0.0, 0.0 };
			return Multiply(Multiply([1, 0, 0, 1, cx, cy], turn), [1, 0, 0, 1, -cx, -cy]);
		}

		/// <summary>m1 then m2 applied inside it (SVG: the outer transform times the inner).</summary>
		internal static double[] Multiply(double[] m1, double[] m2) =>
		[
			(m1[0] * m2[0]) + (m1[2] * m2[1]),
			(m1[1] * m2[0]) + (m1[3] * m2[1]),
			(m1[0] * m2[2]) + (m1[2] * m2[3]),
			(m1[1] * m2[2]) + (m1[3] * m2[3]),
			(m1[0] * m2[4]) + (m1[2] * m2[5]) + m1[4],
			(m1[1] * m2[4]) + (m1[3] * m2[5]) + m1[5],
		];
	}
}
