using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace ApplicationServices;

public static class AudiobookshelfApiService
{
	public record Library(string Id, string Name, string MediaType, List<Folder> Folders);
	public record Folder(string Id, string FullPath);
	public enum UploadResult { Success, AlreadyExists, Failed }

	/// <summary>
	/// Audiobookshelf client (browser) route segments. If present in a pasted URL,
	/// everything from that segment onward is UI path, not part of the API base.
	/// </summary>
	private static readonly HashSet<string> ClientRouteSegments = new(StringComparer.OrdinalIgnoreCase)
	{
		"library",
		"item",
		"collection",
		"playlist",
		"author",
		"series",
		"audiobook",
		"config",
		"login",
		"account",
	};

	/// <summary>
	/// Normalize a user-supplied Audiobookshelf server URL to an API base address.
	/// Compensates for common mistakes such as pasting a browser library/item page URL,
	/// including a trailing <c>/api</c> path, query strings, or omitting the scheme.
	/// </summary>
	/// <returns>Absolute http(s) base URL with no trailing slash.</returns>
	public static string NormalizeServerUrl(string? serverUrl)
	{
		if (string.IsNullOrWhiteSpace(serverUrl))
			throw new ArgumentException("Server URL is required.", nameof(serverUrl));

		var trimmed = serverUrl.Trim();

		// Allow host:port without a scheme (common when copying from docs or local setups).
		if (!trimmed.Contains("://", StringComparison.Ordinal))
			trimmed = "http://" + trimmed;

		if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
			|| (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
			|| string.IsNullOrWhiteSpace(uri.Host))
		{
			throw new ArgumentException(
				"Server URL must be an absolute http(s) address, for example http://localhost:13378 or https://abs.example.com",
				nameof(serverUrl));
		}

		var path = StripClientRoutesAndApi(uri.AbsolutePath);
		var authority = uri.GetLeftPart(UriPartial.Authority);
		if (string.IsNullOrEmpty(path) || path == "/")
			return authority;

		return authority + path.TrimEnd('/');
	}

	private static string StripClientRoutesAndApi(string absolutePath)
	{
		if (string.IsNullOrEmpty(absolutePath) || absolutePath == "/")
			return "";

		var parts = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		var prefixLength = 0;

		for (var i = 0; i < parts.Length; i++)
		{
			var part = parts[i];

			// Trailing or mid-path /api/... means the user included the API root.
			if (string.Equals(part, "api", StringComparison.OrdinalIgnoreCase))
				break;

			if (ClientRouteSegments.Contains(part))
				break;

			prefixLength += 1 + part.Length;
		}

		if (prefixLength <= 0)
			return "";

		return absolutePath[..prefixLength];
	}

	private static HttpClient CreateClient(string serverUrl)
	{
		var normalized = NormalizeServerUrl(serverUrl);
		var client = new HttpClient();
		client.BaseAddress = new Uri(normalized + "/");
		client.Timeout = TimeSpan.FromSeconds(30);
		return client;
	}

	public static async Task<List<Library>> GetLibrariesAsync(string serverUrl, string apiToken)
	{
		var normalizedUrl = NormalizeServerUrl(serverUrl);
		apiToken = AudiobookshelfTokenStorage.DecryptToken(apiToken) ?? "";

		using var client = CreateClient(normalizedUrl);
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

		HttpResponseMessage response;
		try
		{
			response = await client.GetAsync("api/libraries");
		}
		catch (TaskCanceledException ex)
		{
			throw new HttpRequestException(
				$"Timed out connecting to Audiobookshelf at {normalizedUrl}. Check the Server URL and that the server is running.",
				ex);
		}
		catch (HttpRequestException ex)
		{
			throw new HttpRequestException(
				$"Could not reach Audiobookshelf at {normalizedUrl}. Check the Server URL, network, and that the server is running. Details: {ex.Message}",
				ex);
		}

		if (!response.IsSuccessStatusCode)
		{
			var errorBody = await response.Content.ReadAsStringAsync();
			throw CreateLibrariesFetchException(normalizedUrl, response.StatusCode, errorBody);
		}

		var json = await response.Content.ReadAsStringAsync();
		var obj = JObject.Parse(json);
		var libraries = obj["libraries"] as JArray ?? new JArray();

		var allLibraries = libraries.Select(l => new Library(
			l["id"]?.Value<string>() ?? "",
			l["name"]?.Value<string>() ?? "",
			l["mediaType"]?.Value<string>() ?? "book",
			(l["folders"] as JArray ?? new JArray())
				.Select(f => new Folder(
					f["id"]?.Value<string>() ?? "",
					f["fullPath"]?.Value<string>() ?? ""))
				.ToList()))
			.ToList();

		// Only return libraries with book media type
		return allLibraries.Where(l => string.Equals(l.MediaType, "book", StringComparison.OrdinalIgnoreCase)).ToList();
	}

	private static HttpRequestException CreateLibrariesFetchException(string normalizedUrl, HttpStatusCode statusCode, string errorBody)
	{
		var code = (int)statusCode;
		var bodyPreview = SummarizeErrorBody(errorBody);

		if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
		{
			return new HttpRequestException(
				$"Audiobookshelf rejected the API token ({code} {statusCode}). " +
				$"Check that the token is correct and not expired. Server: {normalizedUrl}");
		}

		if (statusCode == HttpStatusCode.NotFound)
		{
			return new HttpRequestException(
				$"Audiobookshelf returned 404 when calling {normalizedUrl}/api/libraries. " +
				"Server URL should be the Audiobookshelf base address only " +
				"(for example http://localhost:13378 or https://abs.example.com), " +
				"not a library or book page from the browser. " +
				"If Audiobookshelf is served under a subpath, include that subpath " +
				$"(for example http://host/audiobookshelf). Response: {bodyPreview}");
		}

		return new HttpRequestException(
			$"Audiobookshelf API returned {code} ({statusCode}) when fetching libraries from {normalizedUrl}. Response: {bodyPreview}");
	}

	private static string SummarizeErrorBody(string errorBody)
	{
		if (string.IsNullOrWhiteSpace(errorBody))
			return "(empty)";

		var trimmed = errorBody.Trim();
		const int maxLen = 280;
		if (trimmed.Length <= maxLen)
			return trimmed;

		return trimmed[..maxLen] + "...";
	}

	public sealed record ConnectResult(
		bool Success,
		string StatusMessage,
		string? NormalizedServerUrl,
		bool ServerUrlAdjusted,
		IReadOnlyList<Library> Libraries);

	/// <summary>
	/// Normalize URL, fetch book libraries, and produce UI status text.
	/// Callers apply busy-state and bind <see cref="ConnectResult.Libraries"/> to controls.
	/// </summary>
	public static async Task<ConnectResult> ConnectAsync(string? serverUrl, string? apiToken)
	{
		if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(apiToken))
		{
			return new ConnectResult(
				Success: false,
				StatusMessage: "Please enter both server URL and API token.",
				NormalizedServerUrl: null,
				ServerUrlAdjusted: false,
				Libraries: []);
		}

		string normalizedUrl;
		bool urlAdjusted;
		try
		{
			var trimmedUrl = serverUrl.Trim();
			normalizedUrl = NormalizeServerUrl(trimmedUrl);
			urlAdjusted = !string.Equals(trimmedUrl.TrimEnd('/'), normalizedUrl, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex)
		{
			return new ConnectResult(
				Success: false,
				StatusMessage: $"Connection failed: {ex.Message}",
				NormalizedServerUrl: null,
				ServerUrlAdjusted: false,
				Libraries: []);
		}

		try
		{
			var libraries = await GetLibrariesAsync(normalizedUrl, apiToken.Trim());
			if (libraries.Count == 0)
			{
				return new ConnectResult(
					Success: false,
					StatusMessage: "No book libraries found. Check your settings.",
					NormalizedServerUrl: normalizedUrl,
					ServerUrlAdjusted: urlAdjusted,
					Libraries: []);
			}

			return new ConnectResult(
				Success: true,
				StatusMessage: FormatConnectedStatus(libraries.Count, urlAdjusted),
				NormalizedServerUrl: normalizedUrl,
				ServerUrlAdjusted: urlAdjusted,
				Libraries: libraries);
		}
		catch (Exception ex)
		{
			return new ConnectResult(
				Success: false,
				StatusMessage: $"Connection failed: {ex.Message}",
				NormalizedServerUrl: normalizedUrl,
				ServerUrlAdjusted: urlAdjusted,
				Libraries: []);
		}
	}

	public static string FormatConnectedStatus(int libraryCount, bool urlAdjusted)
	{
		var libraryWord = libraryCount == 1 ? "library" : "libraries";
		var message = $"Connected. Found {libraryCount} {libraryWord}.";
		if (urlAdjusted)
			message += " Server URL adjusted to the API base address.";
		return message;
	}

	/// <summary>
	/// Normalize for persistence when possible; otherwise keep the trimmed raw value
	/// so the user can fix it on the next connect attempt.
	/// </summary>
	public static string TryNormalizeServerUrlForSave(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
			return url?.Trim() ?? "";

		try
		{
			return NormalizeServerUrl(url);
		}
		catch (ArgumentException)
		{
			return url.Trim();
		}
	}

	/// <summary>
	/// Whether the library already holds this book.
	/// <para/>
	/// Candidates come from Audiobookshelf's library search, which finds its query as a substring of an
	/// item's title, subtitle, ASIN or ISBN. The search is asked for the ASIN, then for the title with and
	/// without its subtitle. <see cref="ClassifyCandidate"/> decides whether a candidate is the same book.
	/// <para/>
	/// Audiobookshelf fills an item's ASIN from the audio file's tags or from an <c>[ASIN]</c> in the folder
	/// name. An ASIN that appears only in a file name is not in the item's metadata and is not matched as one.
	/// </summary>
	public static async Task<bool> BookExistsAsync(string serverUrl, string apiToken, string libraryId, string title, string? author = null, string? asin = null)
	{
		apiToken = AudiobookshelfTokenStorage.DecryptToken(apiToken) ?? "";
		using var client = CreateClient(serverUrl);
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
		client.Timeout = TimeSpan.FromSeconds(30);

		var normalizedTitle = NormalizeTitle(title);
		var baseTitle = GetBaseTitle(normalizedTitle);

		Serilog.Log.Logger.Debug("Audiobookshelf duplicate check: looking for '{Title}' (base: '{BaseTitle}') in library {LibraryId}", normalizedTitle, baseTitle, libraryId);

		var seen = new HashSet<string>(StringComparer.Ordinal);

		// Runs one search and reports whether any item it has not looked at before is this book.
		async Task<bool> SearchFindsBookAsync(string query, int limit)
		{
			foreach (var candidate in await SearchLibraryAsync(client, libraryId, query, limit))
			{
				if (!seen.Add(candidate.Id))
					continue;

				var match = ClassifyCandidate(
					normalizedTitle, author, asin,
					candidate.Title, candidate.FullTitle, candidate.Authors, candidate.Asin);

				if (match != DuplicateMatch.None)
				{
					Serilog.Log.Logger.Information("Audiobookshelf duplicate check: found match for '{Title}' by {MatchedBy}: '{CandidateTitle}' (id={ItemId})", title, match, candidate.Title, candidate.Id);
					return true;
				}

				Serilog.Log.Logger.Debug("Audiobookshelf duplicate check: '{CandidateTitle}' (id={ItemId}) is not '{Title}'", candidate.Title, candidate.Id, normalizedTitle);
			}
			return false;
		}

		try
		{
			// The ASIN identifies the book whatever the server calls it, so try it before the title.
			if (!string.IsNullOrWhiteSpace(asin) && await SearchFindsBookAsync(asin.Trim(), 10))
				return true;

			// Search with base title first (shorter = broader matches, better recall)
			if (await SearchFindsBookAsync(baseTitle, 20))
				return true;

			// Also search with full title to catch cases where ABS stores the longer/subtitled version
			if (!string.Equals(normalizedTitle, baseTitle, StringComparison.Ordinal) && await SearchFindsBookAsync(normalizedTitle, 10))
				return true;

			Serilog.Log.Logger.Debug("Audiobookshelf duplicate check: '{Title}' not found among {Count} candidate(s) in library {LibraryId}", normalizedTitle, seen.Count, libraryId);
			return false;
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Debug(ex, "Audiobookshelf duplicate check failed for '{Title}' in library {LibraryId}", title, libraryId);
			throw; // Let caller decide whether to proceed with upload
		}
	}

	private record SearchCandidate(string Id, string Title, string FullTitle, List<string> Authors, string? Asin);

	private static async Task<List<SearchCandidate>> SearchLibraryAsync(HttpClient client, string libraryId, string query, int limit)
	{
		var url = $"api/libraries/{Uri.EscapeDataString(libraryId)}/search?q={Uri.EscapeDataString(query)}&limit={limit}";
		var response = await client.GetAsync(url);
		if (!response.IsSuccessStatusCode)
		{
			var errorBody = await response.Content.ReadAsStringAsync();
			throw new HttpRequestException($"Audiobookshelf search returned {(int)response.StatusCode} for query '{query}'. Body: {errorBody}");
		}

		var json = await response.Content.ReadAsStringAsync();
		var obj = JObject.Parse(json);
		var books = obj["book"] as JArray ?? new JArray();

		var results = new List<SearchCandidate>();
		foreach (var entry in books)
		{
			var item = entry["libraryItem"] ?? entry;
			var id = item["id"]?.Value<string>() ?? entry["id"]?.Value<string>() ?? "";
			var itemTitle = item["media"]?["metadata"]?["title"]?.Value<string>()?.Replace("\u00A0", " ").Trim();
			var itemSubtitle = item["media"]?["metadata"]?["subtitle"]?.Value<string>()?.Replace("\u00A0", " ").Trim();

			var itemFullTitle = string.IsNullOrWhiteSpace(itemSubtitle)
				? itemTitle
				: $"{itemTitle}: {itemSubtitle}";

			var authors = item["media"]?["metadata"]?["authors"] as JArray;
			var authorList = authors?.Select(a => a["name"]?.Value<string>()?.Trim() ?? "")
				.Where(n => !string.IsNullOrWhiteSpace(n))
				.ToList() ?? [];

			results.Add(new SearchCandidate(
				id,
				NormalizeTitle(itemTitle),
				NormalizeTitle(itemFullTitle),
				authorList,
				item["media"]?["metadata"]?["asin"]?.Value<string>()));
		}

		return results;
	}

	/// <summary>What identified a server item as the book being uploaded.</summary>
	internal enum DuplicateMatch
	{
		None,
		/// <summary>Same ASIN.</summary>
		Asin,
		/// <summary>Same title and an author in common.</summary>
		TitleAndAuthor,
	}

	/// <summary>
	/// Decides whether a server item is the book being uploaded.
	/// <para/>
	/// 1) ASIN. The same ASIN is the same book whatever else differs. Two different ASINs are two different
	/// books whatever else agrees: the same title and author under another ASIN is another edition or narration.
	/// <para/>
	/// 2) Title and author, when either side has no ASIN. The same title with an author in common is the
	/// same book. See <see cref="TitlesMatch"/> and <see cref="AuthorsCompatible"/>.
	/// </summary>
	internal static DuplicateMatch ClassifyCandidate(
		string? libationTitle,
		string? libationAuthor,
		string? libationAsin,
		string? absTitle,
		string? absFullTitle,
		IReadOnlyList<string>? absAuthors,
		string? absAsin)
	{
		if (AsinsMatch(libationAsin, absAsin))
			return DuplicateMatch.Asin;
		if (AsinsDiffer(libationAsin, absAsin))
			return DuplicateMatch.None;

		return TitlesMatch(libationTitle, absTitle, absFullTitle) && AuthorsCompatible(libationAuthor, absAuthors)
			? DuplicateMatch.TitleAndAuthor
			: DuplicateMatch.None;
	}

	internal static bool AsinsMatch(string? libationAsin, string? absAsin)
		=> !string.IsNullOrWhiteSpace(libationAsin)
		&& !string.IsNullOrWhiteSpace(absAsin)
		&& string.Equals(libationAsin.Trim(), absAsin.Trim(), StringComparison.OrdinalIgnoreCase);

	/// <summary>Both sides have an ASIN and they are not the same one.</summary>
	internal static bool AsinsDiffer(string? libationAsin, string? absAsin)
		=> !string.IsNullOrWhiteSpace(libationAsin)
		&& !string.IsNullOrWhiteSpace(absAsin)
		&& !AsinsMatch(libationAsin, absAsin);

	/// <summary>
	/// Whether an Audiobookshelf title is the same book as Libation's "Title: Subtitle".
	/// <para/>
	/// The server's title matches when it equals Libation's full title or the part before the subtitle,
	/// or when the server's "title: subtitle" equals Libation's full title. Case, accents, punctuation
	/// and spacing are ignored.
	/// <para/>
	/// A trailing qualifier in brackets, such as "(Spirit Animals, Book 1)", is ignored when only one side
	/// has one. Two titles that each carry a different qualifier stay different books.
	/// </summary>
	/// <param name="libationTitle">Libation's title, with its subtitle if it has one.</param>
	/// <param name="absTitle">The server's title.</param>
	/// <param name="absFullTitle">The server's "title: subtitle", or its title when it has no subtitle.</param>
	internal static bool TitlesMatch(string? libationTitle, string? absTitle, string? absFullTitle)
	{
		var full = NormalizeTitle(libationTitle);
		var title = NormalizeTitle(absTitle);
		if (string.IsNullOrWhiteSpace(full) || string.IsNullOrWhiteSpace(title))
			return false;

		var baseTitle = GetBaseTitle(full);

		return SameTitle(title, full)
			|| SameTitle(title, baseTitle)
			|| SameTitle(NormalizeTitle(absFullTitle), full);
	}

	private static bool SameTitle(string absTitle, string libationTitle)
	{
		var absKey = ComparisonKey(absTitle);
		var libationKey = ComparisonKey(libationTitle);
		if (absKey.Length == 0 || libationKey.Length == 0)
			return false;
		if (absKey == libationKey)
			return true;

		var absStripped = StripTrailingQualifiers(absTitle);
		var libationStripped = StripTrailingQualifiers(libationTitle);

		// Only one side may have had a qualifier. "Title (Book 1)" and "Title (Book 2)" are different books.
		if (absStripped.Length != absTitle.Length && libationStripped.Length != libationTitle.Length)
			return false;

		var absStrippedKey = ComparisonKey(absStripped);
		return absStrippedKey.Length > 0 && absStrippedKey == ComparisonKey(libationStripped);
	}

	/// <summary>Removes bracketed groups from the end of a title: "Wild Born (Spirit Animals, Book 1)" becomes "Wild Born".</summary>
	private static string StripTrailingQualifiers(string title)
	{
		var result = title.TrimEnd();
		while (result.Length > 0 && result[^1] is ')' or ']')
		{
			var open = result.LastIndexOf(result[^1] == ')' ? '(' : '[');
			if (open <= 0)
				break;
			result = result[..open].TrimEnd();
		}
		return result;
	}

	/// <summary>
	/// Reduces a title or name to its letters and digits, lower-cased and without accents, so that
	/// "R.A. Salvatore" and "R. A. Salvatore" compare equal. "&amp;" counts as "and".
	/// </summary>
	private static string ComparisonKey(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return "";

		var sb = new StringBuilder(value.Length);
		foreach (var c in value.Replace("&", " and ").Normalize(NormalizationForm.FormKD))
		{
			if (char.IsLetterOrDigit(c))
				sb.Append(char.ToLowerInvariant(c));
		}
		return sb.ToString();
	}

	/// <summary>
	/// Whether any author on the server is one of Libation's authors. Case, accents, punctuation and
	/// spacing are ignored, so the spacing of initials does not matter.
	/// </summary>
	internal static bool AuthorsCompatible(string? libationAuthor, IReadOnlyList<string>? absAuthors)
	{
		// If either side has no author info, we can't use authors to disambiguate
		if (string.IsNullOrWhiteSpace(libationAuthor))
			return true;
		if (absAuthors == null || absAuthors.Count == 0)
			return true;

		var libationAuthors = libationAuthor.Split(',', StringSplitOptions.RemoveEmptyEntries)
			.Select(ComparisonKey)
			.Where(a => a.Length > 0)
			.ToHashSet(StringComparer.Ordinal);

		if (libationAuthors.Count == 0)
			return true;

		return absAuthors.Select(ComparisonKey).Any(libationAuthors.Contains);
	}

	private static string NormalizeTitle(string? title)
	{
		if (string.IsNullOrWhiteSpace(title))
			return title ?? "";

		var normalized = title
			.Replace("\u00A0", " ")
			.Replace("(Unabridged)", "", StringComparison.OrdinalIgnoreCase)
			.Replace("(Abridged)", "", StringComparison.OrdinalIgnoreCase)
			.Trim();

		// Collapse multiple spaces to single space
		while (normalized.Contains("  ", StringComparison.Ordinal))
			normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);

		return normalized;
	}

	private static string GetBaseTitle(string fullTitle)
	{
		if (string.IsNullOrWhiteSpace(fullTitle))
			return fullTitle;

		var colonIndex = fullTitle.IndexOf(": ", StringComparison.Ordinal);
		return colonIndex > 0 ? fullTitle[..colonIndex].Trim() : fullTitle;
	}

	public static async Task<UploadResult> UploadBookAsync(
		string serverUrl,
		string apiToken,
		string libraryId,
		string folderId,
		string title,
		string? author,
		string? series,
		IEnumerable<string> filePaths,
		string? asin = null)
	{
		apiToken = AudiobookshelfTokenStorage.DecryptToken(apiToken) ?? "";

		// Pre-check for existing item
		try
		{
			if (await BookExistsAsync(serverUrl, apiToken, libraryId, title, author, asin))
			{
				Serilog.Log.Logger.Information("Skipping Audiobookshelf upload: book '{Title}' already exists in library {LibraryId}", title, libraryId);
				return UploadResult.AlreadyExists;
			}
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Warning(ex, "Pre-check for existing book on Audiobookshelf failed; will still attempt upload");
			// Continue to upload attempt below
		}

		using var client = CreateClient(serverUrl);
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
		client.Timeout = TimeSpan.FromMinutes(30);

		using var form = new MultipartFormDataContent();
		form.Add(new StringContent(title), "title");

		if (!string.IsNullOrWhiteSpace(author))
			form.Add(new StringContent(author), "author");
		if (!string.IsNullOrWhiteSpace(series))
			form.Add(new StringContent(series), "series");

		form.Add(new StringContent(libraryId), "library");
		form.Add(new StringContent(folderId), "folder");

		int fileIndex = 0;
		var streams = new List<Stream>();
		try
		{
			foreach (var path in filePaths.Where(File.Exists))
			{
				var stream = File.OpenRead(path);
				streams.Add(stream);
				var fileContent = new StreamContent(stream);
				fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
				form.Add(fileContent, fileIndex.ToString(), Path.GetFileName(path));
				fileIndex++;
			}

			if (fileIndex == 0)
			{
				Serilog.Log.Logger.Warning("No audio files found to upload to Audiobookshelf for '{Title}'", title);
				return UploadResult.Failed;
			}

			var response = await client.PostAsync("api/upload", form);

			if (response.IsSuccessStatusCode)
				return UploadResult.Success;

			var responseBody = await response.Content.ReadAsStringAsync();
			Serilog.Log.Logger.Error("Audiobookshelf upload failed for '{Title}' with status {(int)response.StatusCode} ({StatusCode}). Response body: {ResponseBody}",
				title, (int)response.StatusCode, response.StatusCode, responseBody);

			// Treat "already exists" 500 errors as skip/success
			if (response.StatusCode == System.Net.HttpStatusCode.InternalServerError
				&& responseBody.Contains("already exists", StringComparison.OrdinalIgnoreCase))
			{
				return UploadResult.AlreadyExists;
			}

			return UploadResult.Failed;
		}
		finally
		{
			foreach (var stream in streams)
			{
				try { stream.Dispose(); } catch { /* ignored */ }
			}
		}
	}
}
