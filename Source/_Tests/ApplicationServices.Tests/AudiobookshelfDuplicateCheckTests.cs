using AssertionHelper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ApplicationServices.Tests;

[TestClass]
public class AudiobookshelfDuplicateCheckTests
{
	[TestMethod]
	// Libation title, server title, server "title: subtitle"
	[DataRow("Wild Born", "Wild Born", "Wild Born")]
	[DataRow("Wild Born", "wild born", "wild born")]
	[DataRow("Wild Born: Spirit Animals, Book 1", "Wild Born", "Wild Born")]
	[DataRow("Wild Born: Spirit Animals, Book 1", "Wild Born", "Wild Born: Spirit Animals, Book 1")]
	[DataRow("Wild Born: Spirit Animals, Book 1", "Wild Born: Spirit Animals, Book 1", "Wild Born: Spirit Animals, Book 1")]
	[DataRow("Wild Born (Unabridged)", "Wild Born", "Wild Born")]
	// A series or edition qualifier on one side only
	[DataRow("Wild Born: Spirit Animals, Book 1", "Wild Born (Spirit Animals, Book 1)", "Wild Born (Spirit Animals, Book 1)")]
	[DataRow("Wild Born", "Wild Born [Spirit Animals 1]", "Wild Born [Spirit Animals 1]")]
	[DataRow("Le Petit Prince (French Edition)", "Le Petit Prince", "Le Petit Prince")]
	[DataRow("Wild Born (Spirit Animals, Book 1)", "Wild Born", "Wild Born: A Novel")]
	// Punctuation, spacing and accents
	[DataRow("Harry Potter and the Sorcerer's Stone", "Harry Potter and the Sorcerer’s Stone", "Harry Potter and the Sorcerer’s Stone")]
	[DataRow("Spider-Man: Into the Spider-Verse", "Spider Man", "Spider Man")]
	[DataRow("Les Misérables", "Les Miserables", "Les Miserables")]
	[DataRow("Dune", "Dune ", "Dune :  ")]
	[DataRow("Fire & Blood", "Fire and Blood", "Fire and Blood")]
	public void TitlesMatch_accepts_the_same_book(string libationTitle, string absTitle, string absFullTitle)
	{
		AudiobookshelfApiService.TitlesMatch(libationTitle, absTitle, absFullTitle).Should().BeTrue();
	}

	[TestMethod]
	[DataRow("Wild Born", "Wild Born Again", "Wild Born Again")]
	[DataRow("Wild Born", "Hunted", "Hunted")]
	[DataRow("Wild Born: Spirit Animals, Book 1", "Spirit Animals", "Spirit Animals")]
	// Both sides carry a qualifier and they differ
	[DataRow("The Collection (Book 1)", "The Collection (Book 2)", "The Collection (Book 2)")]
	[DataRow("The Collection (Book 1)", "The Collection [Book 2]", "The Collection [Book 2]")]
	// Nothing left to compare
	[DataRow("(Unabridged)", "(Abridged)", "(Abridged)")]
	[DataRow("Dune", "", "")]
	[DataRow("Dune", null, null)]
	[DataRow("", "Dune", "Dune")]
	[DataRow("(1)", "(2)", "(2)")]
	[DataRow("???", "!!!", "!!!")]
	public void TitlesMatch_rejects_different_books(string? libationTitle, string? absTitle, string? absFullTitle)
	{
		AudiobookshelfApiService.TitlesMatch(libationTitle, absTitle, absFullTitle).Should().BeFalse();
	}

	[TestMethod]
	[DataRow("R.A. Salvatore", "R. A. Salvatore")]
	[DataRow("R. A. Salvatore", "R.A. Salvatore")]
	[DataRow("J.R.R. Tolkien", "J. R. R. Tolkien")]
	[DataRow("r.a. salvatore", "R.A. SALVATORE")]
	[DataRow("Gabriel García Márquez", "Gabriel Garcia Marquez")]
	[DataRow("Brandon Sanderson, Janci Patterson", "Janci Patterson")]
	[DataRow("Brandon Sanderson", "Brandon Sanderson")]
	[DataRow("", "Brandon Sanderson")]
	[DataRow(null, "Brandon Sanderson")]
	[DataRow(", ,", "Brandon Sanderson")]
	public void AuthorsCompatible_accepts_the_same_author(string? libationAuthors, string absAuthor)
	{
		AudiobookshelfApiService.AuthorsCompatible(libationAuthors, [absAuthor]).Should().BeTrue();
	}

	[TestMethod]
	public void AuthorsCompatible_accepts_a_server_item_without_authors()
	{
		AudiobookshelfApiService.AuthorsCompatible("Brandon Sanderson", []).Should().BeTrue();
		AudiobookshelfApiService.AuthorsCompatible("Brandon Sanderson", null).Should().BeTrue();
	}

	[TestMethod]
	[DataRow("R.A. Salvatore", "Bob Salvatore")]
	[DataRow("Brandon Sanderson", "Brandon Mull")]
	[DataRow("Brandon Sanderson, Janci Patterson", "Stephen King")]
	public void AuthorsCompatible_rejects_a_different_author(string libationAuthors, string absAuthor)
	{
		AudiobookshelfApiService.AuthorsCompatible(libationAuthors, [absAuthor]).Should().BeFalse();
	}

	[TestMethod]
	[DataRow("B0TEST0001", "B0TEST0001", true)]
	[DataRow("B0TEST0001", " b0test0001 ", true)]
	[DataRow("B0TEST0001", "B0TEST0002", false)]
	[DataRow("B0TEST0001", "", false)]
	[DataRow("B0TEST0001", null, false)]
	[DataRow("", "", false)]
	[DataRow(null, null, false)]
	public void AsinsMatch_needs_the_same_asin_on_both_sides(string? libationAsin, string? absAsin, bool expected)
	{
		AudiobookshelfApiService.AsinsMatch(libationAsin, absAsin).Should().Be(expected);
	}

	[TestMethod]
	[DataRow("B0TEST0001", "B0TEST0002", true)]
	[DataRow("B0TEST0001", " b0test0001 ", false)]
	[DataRow("B0TEST0001", "", false)]
	[DataRow("B0TEST0001", null, false)]
	[DataRow("", "B0TEST0002", false)]
	[DataRow(null, "B0TEST0002", false)]
	[DataRow(null, null, false)]
	public void AsinsDiffer_needs_two_different_asins(string? libationAsin, string? absAsin, bool expected)
	{
		AudiobookshelfApiService.AsinsDiffer(libationAsin, absAsin).Should().Be(expected);
	}

	[TestMethod]
	public async Task BookExistsAsync_treats_the_same_title_under_another_asin_as_a_different_edition()
	{
		using var server = FakeAudiobookshelf.Start(
			new FakeItem("Dune", "Frank Herbert", Asin: "B0TEST0002"));

		(await BookExistsAsync(server, "Dune", "Frank Herbert", "B0TEST0001")).Should().BeFalse();
		// Without an ASIN to compare, title and author are all there is to go on.
		(await BookExistsAsync(server, "Dune", "Frank Herbert", asin: null)).Should().BeTrue();
	}

	[TestMethod]
	public async Task BookExistsAsync_finds_the_matching_edition_among_several()
	{
		using var server = FakeAudiobookshelf.Start(
			new FakeItem("Dune", "Frank Herbert", Asin: "B0TEST0002"),
			new FakeItem("Dune", "Frank Herbert"));

		// The first item is another edition; the second has no ASIN and matches by title and author.
		(await BookExistsAsync(server, "Dune", "Frank Herbert", "B0TEST0001")).Should().BeTrue();
	}

	[TestMethod]
	public async Task BookExistsAsync_finds_a_book_by_asin_when_the_titles_differ()
	{
		using var server = FakeAudiobookshelf.Start(
			new FakeItem("Le Petit Prince", "Antoine de Saint-Exupéry", Asin: "B0TEST0001"));

		var exists = await BookExistsAsync(server, "The Little Prince (French Edition)", "Somebody Else", "B0TEST0001");

		exists.Should().BeTrue();
		server.Queries.Should().HaveCount(1);
		server.Queries[0].Should().Be("B0TEST0001");
	}

	[TestMethod]
	public async Task BookExistsAsync_ignores_a_search_hit_with_another_asin()
	{
		// Audiobookshelf matches the ASIN as a substring, so a hit is not proof.
		using var server = FakeAudiobookshelf.Start(
			new FakeItem("Some Other Book", "Some Author", Asin: "B0TEST00012"));

		var exists = await BookExistsAsync(server, "Wild Born", "Brandon Mull", "B0TEST0001");

		exists.Should().BeFalse();
	}

	[TestMethod]
	public async Task BookExistsAsync_falls_back_to_title_and_author_when_the_server_has_no_asin()
	{
		using var server = FakeAudiobookshelf.Start(
			new FakeItem("Wild Born (Spirit Animals, Book 1)", "Brandon Mull"));

		var exists = await BookExistsAsync(server, "Wild Born: Spirit Animals, Book 1", "Brandon Mull", "B0TEST0001");

		exists.Should().BeTrue();
		// ASIN first, then the title searches.
		server.Queries[0].Should().Be("B0TEST0001");
		Assert.IsTrue(server.Queries.Count > 1);
	}

	[TestMethod]
	public async Task BookExistsAsync_falls_back_to_title_and_author_when_the_asin_is_only_in_a_file_name()
	{
		// The server found no ASIN in the tags or the folder name, so its metadata has none,
		// even though libraryFiles shows it in the file name.
		using var server = FakeAudiobookshelf.Start(
			new FakeItem("Wild Born", "Brandon Mull", FileName: "Wild Born [B0TEST0001].m4b"));

		(await BookExistsAsync(server, "Wild Born", "Brandon Mull", "B0TEST0001")).Should().BeTrue();
		(await BookExistsAsync(server, "Another Title Entirely", "Brandon Mull", "B0TEST0001")).Should().BeFalse();
	}

	[TestMethod]
	public async Task BookExistsAsync_matches_title_and_author_without_an_asin()
	{
		using var server = FakeAudiobookshelf.Start(
			new FakeItem("The Crystal Shard", "R. A. Salvatore", Subtitle: "The Icewind Dale Trilogy, Book 1"));

		(await BookExistsAsync(server, "The Crystal Shard: The Icewind Dale Trilogy, Book 1", "R.A. Salvatore", asin: null)).Should().BeTrue();
		(await BookExistsAsync(server, "The Crystal Shard", "Somebody Else", asin: null)).Should().BeFalse();
		(await BookExistsAsync(server, "Streams of Silver", "R.A. Salvatore", asin: null)).Should().BeFalse();
	}

	[TestMethod]
	// Audible minutes, server seconds
	[DataRow(600, 36000d, AudiobookshelfApiService.LengthComparison.Same)]
	[DataRow(600, 36119d, AudiobookshelfApiService.LengthComparison.Same)]
	[DataRow(600, 35881d, AudiobookshelfApiService.LengthComparison.Same)]
	[DataRow(600, 36360d, AudiobookshelfApiService.LengthComparison.Same)] // 1% of ten hours
	[DataRow(10, 480d, AudiobookshelfApiService.LengthComparison.Same)] // two minutes on a short book
	[DataRow(600, 36400d, AudiobookshelfApiService.LengthComparison.Inconclusive)]
	[DataRow(600, 33000d, AudiobookshelfApiService.LengthComparison.Inconclusive)]
	[DataRow(10, 850d, AudiobookshelfApiService.LengthComparison.Inconclusive)] // 40% apart but under five minutes
	[DataRow(600, 30000d, AudiobookshelfApiService.LengthComparison.Different)]
	[DataRow(600, 18000d, AudiobookshelfApiService.LengthComparison.Different)]
	[DataRow(600, 42000d, AudiobookshelfApiService.LengthComparison.Different)]
	[DataRow(600, null, AudiobookshelfApiService.LengthComparison.Unknown)]
	[DataRow(600, 0d, AudiobookshelfApiService.LengthComparison.Unknown)]
	[DataRow(0, 36000d, AudiobookshelfApiService.LengthComparison.Unknown)]
	[DataRow(null, 36000d, AudiobookshelfApiService.LengthComparison.Unknown)]
	public void CompareLengths_sorts_lengths_into_same_inconclusive_and_different(int? libationMinutes, double? absSeconds, object expected)
	{
		Assert.AreEqual((AudiobookshelfApiService.LengthComparison)expected, AudiobookshelfApiService.CompareLengths(libationMinutes, absSeconds));
	}

	[TestMethod]
	[DataRow("Wild Born", "Wlid Born")]
	[DataRow("Wild Born", "Wild Borne")]
	[DataRow("Wild Born: Spirit Animals, Book 1", "Wild Bonr (Spirit Animals, Book 1)")]
	[DataRow("The Crystal Shard", "The Cristal Shard")]
	[DataRow("Harry Potter and the Sorcerer's Stone", "Harry Potter and the Sorceror's Stone")]
	public void TitleSimilarity_is_high_for_a_misspelled_title(string libationTitle, string absTitle)
	{
		Assert.IsTrue(AudiobookshelfApiService.TitleSimilarity(libationTitle, absTitle, absTitle) >= AudiobookshelfApiService.SimilarityThreshold);
	}

	[TestMethod]
	[DataRow("Wild Born", "Wild Fire")]
	[DataRow("Dune", "Done")]
	[DataRow("The Collection, Book 1", "The Collection, Book 2")]
	[DataRow("The Collection (Book 1)", "The Collection (Book 2)")]
	[DataRow("Area 51", "Area 52")]
	[DataRow("Catch-22", "Catch")]
	[DataRow("Wild Born", "")]
	[DataRow("", "Wild Born")]
	public void TitleSimilarity_is_low_for_another_title(string libationTitle, string absTitle)
	{
		Assert.IsTrue(AudiobookshelfApiService.TitleSimilarity(libationTitle, absTitle, absTitle) < AudiobookshelfApiService.SimilarityThreshold);
	}

	[TestMethod]
	public void TitleSimilarity_ignores_leading_zeros_in_numbers()
	{
		Assert.IsTrue(AudiobookshelfApiService.TitleSimilarity("The Collection, Book 1", "The Colection, Book 01", null) >= AudiobookshelfApiService.SimilarityThreshold);
	}

	[TestMethod]
	[DataRow("Brandon Mull", "Brandon Mul", true)]
	[DataRow("R.A. Salvatore", "R. A. Salvatroe", true)]
	[DataRow("Brandon Sanderson, Janci Patterson", "Janci Paterson", true)]
	[DataRow("", "Brandon Mull", true)]
	[DataRow("Brandon Mull", "Brandon Sanderson", false)]
	[DataRow("Brandon Mull", "Stephen King", false)]
	public void AuthorsSimilar_allows_a_few_wrong_characters(string libationAuthors, string absAuthor, bool expected)
	{
		AudiobookshelfApiService.AuthorsSimilar(libationAuthors, [absAuthor]).Should().Be(expected);
	}

	[TestMethod]
	public void GetDistinctiveWords_returns_the_longest_words()
	{
		string.Join("|", AudiobookshelfApiService.GetDistinctiveWords("Harry Potter and the Sorcerer's Stone")).Should().Be("Sorcerer|Potter");
		string.Join("|", AudiobookshelfApiService.GetDistinctiveWords("Wild Born")).Should().Be("Wild|Born");
		string.Join("|", AudiobookshelfApiService.GetDistinctiveWords("Dune Dune DUNE")).Should().Be("Dune");
		// Too short to narrow a search, or already searched for as the whole title.
		AudiobookshelfApiService.GetDistinctiveWords("It").Should().HaveCount(0);
		AudiobookshelfApiService.GetDistinctiveWords("Dune").Should().HaveCount(0);
		AudiobookshelfApiService.GetDistinctiveWords(null).Should().HaveCount(0);
	}

	[TestMethod]
	// Libation: title, author, ASIN, minutes. Server: title, author, ASIN, seconds.
	// ASIN decides when both sides have one.
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", 600, "Something Else", "Someone Else", "B0TEST0001", 100d, AudiobookshelfApiService.DuplicateMatch.Asin)]
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", 600, "Dune", "Frank Herbert", "B0TEST0002", 36000d, AudiobookshelfApiService.DuplicateMatch.None)]
	// Same title and author.
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", 600, "Dune", "Frank Herbert", null, 36000d, AudiobookshelfApiService.DuplicateMatch.TitleAndAuthor)]
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", 600, "Dune", "Frank Herbert", null, null, AudiobookshelfApiService.DuplicateMatch.TitleAndAuthor)]
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", null, "Dune", "Frank Herbert", null, 36000d, AudiobookshelfApiService.DuplicateMatch.TitleAndAuthor)]
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", 600, "Dune", "Frank Herbert", null, 33000d, AudiobookshelfApiService.DuplicateMatch.TitleAndAuthor)]
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", 600, "Dune", null, null, null, AudiobookshelfApiService.DuplicateMatch.TitleAndAuthor)]
	// ... but not when the length shows another edition or an incomplete copy.
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", 600, "Dune", "Frank Herbert", null, 18000d, AudiobookshelfApiService.DuplicateMatch.None)]
	// Same title, another author: only with the same length.
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", 600, "Dune", "Unknown", null, 36030d, AudiobookshelfApiService.DuplicateMatch.TitleAndLength)]
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", 600, "Dune", "Unknown", null, 33000d, AudiobookshelfApiService.DuplicateMatch.None)]
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", 600, "Dune", "Unknown", null, null, AudiobookshelfApiService.DuplicateMatch.None)]
	// Misspelled title or author: only with the same length.
	[DataRow("Wild Born", "Brandon Mull", "B0TEST0001", 600, "Wlid Born", "Brandon Mull", null, 36030d, AudiobookshelfApiService.DuplicateMatch.SimilarTitleAndLength)]
	[DataRow("Wild Born", "Brandon Mull", "B0TEST0001", 600, "Wlid Born", "Brandon Mul", null, 36030d, AudiobookshelfApiService.DuplicateMatch.SimilarTitleAndLength)]
	[DataRow("Wild Born", "Brandon Mull", "B0TEST0001", 600, "Wlid Born", null, null, 36030d, AudiobookshelfApiService.DuplicateMatch.SimilarTitleAndLength)]
	[DataRow("Wild Born", "Brandon Mull", null, 600, "Wlid Born", "Brandon Mull", null, 36030d, AudiobookshelfApiService.DuplicateMatch.SimilarTitleAndLength)]
	[DataRow("Wild Born", "Brandon Mull", "B0TEST0001", 600, "Wlid Born", "Brandon Mull", null, 33000d, AudiobookshelfApiService.DuplicateMatch.None)]
	[DataRow("Wild Born", "Brandon Mull", "B0TEST0001", 600, "Wlid Born", "Brandon Mull", null, null, AudiobookshelfApiService.DuplicateMatch.None)]
	[DataRow("Wild Born", "Brandon Mull", "B0TEST0001", null, "Wlid Born", "Brandon Mull", null, 36030d, AudiobookshelfApiService.DuplicateMatch.None)]
	[DataRow("Wild Born", "Brandon Mull", "B0TEST0001", 600, "Wlid Born", "Stephen King", null, 36030d, AudiobookshelfApiService.DuplicateMatch.None)]
	[DataRow("Wild Born", "Brandon Mull", "B0TEST0001", 600, "Wlid Born", "Brandon Mull", "B0TEST0002", 36030d, AudiobookshelfApiService.DuplicateMatch.None)]
	// The next book in a series, even at the same length.
	[DataRow("The Collection, Book 1", "Some Author", "B0TEST0001", 600, "The Collection, Book 2", "Some Author", null, 36000d, AudiobookshelfApiService.DuplicateMatch.None)]
	public void ClassifyCandidate_weighs_asin_then_length_then_title_and_author(
		string libationTitle, string libationAuthor, string? libationAsin, int? libationMinutes,
		string absTitle, string? absAuthor, string? absAsin, double? absSeconds,
		object expected)
	{
		var match = AudiobookshelfApiService.ClassifyCandidate(
			libationTitle, libationAuthor, libationAsin, libationMinutes,
			absTitle, absTitle, absAuthor is null ? [] : [absAuthor], absAsin, absSeconds);

		Assert.AreEqual((AudiobookshelfApiService.DuplicateMatch)expected, match);
	}

	[TestMethod]
	public async Task BookExistsAsync_finds_a_misspelled_title_of_the_same_length()
	{
		using var server = FakeAudiobookshelf.Start(
			new FakeItem("Wlid Born", "Brandon Mul", DurationSeconds: 36030));

		(await BookExistsAsync(server, "Wild Born: Spirit Animals, Book 1", "Brandon Mull", "B0TEST0001", lengthInMinutes: 600)).Should().BeTrue();
		// The title searches find nothing; the search for the word "Born" does.
		CollectionAssert.Contains(server.Queries, "Born");
	}

	[TestMethod]
	public async Task BookExistsAsync_does_not_accept_a_misspelled_title_of_another_length()
	{
		using var server = FakeAudiobookshelf.Start(
			new FakeItem("Wlid Born", "Brandon Mull", DurationSeconds: 30000));

		(await BookExistsAsync(server, "Wild Born", "Brandon Mull", "B0TEST0001", lengthInMinutes: 600)).Should().BeFalse();
	}

	[TestMethod]
	public async Task BookExistsAsync_does_not_search_by_word_without_a_length()
	{
		using var server = FakeAudiobookshelf.Start(
			new FakeItem("Wlid Born", "Brandon Mull", DurationSeconds: 36030));

		(await BookExistsAsync(server, "Wild Born", "Brandon Mull", "B0TEST0001")).Should().BeFalse();
		string.Join("|", server.Queries).Should().Be("B0TEST0001|Wild Born");
	}

	[TestMethod]
	public async Task BookExistsAsync_treats_the_same_title_at_another_length_as_a_different_edition()
	{
		// No ASIN on the server to tell the editions apart, but half the length: abridged, or an incomplete copy.
		using var server = FakeAudiobookshelf.Start(
			new FakeItem("Dune", "Frank Herbert", DurationSeconds: 18000));

		(await BookExistsAsync(server, "Dune", "Frank Herbert", "B0TEST0001", lengthInMinutes: 600)).Should().BeFalse();
		(await BookExistsAsync(server, "Dune", "Frank Herbert", "B0TEST0001")).Should().BeTrue();
	}

	[TestMethod]
	public async Task BookExistsAsync_finds_the_same_title_and_length_under_another_author()
	{
		using var server = FakeAudiobookshelf.Start(
			new FakeItem("Dune", "Unknown Author", DurationSeconds: 36030));

		(await BookExistsAsync(server, "Dune", "Frank Herbert", "B0TEST0001", lengthInMinutes: 600)).Should().BeTrue();
		(await BookExistsAsync(server, "Dune", "Frank Herbert", "B0TEST0001")).Should().BeFalse();
	}

	private static Task<bool> BookExistsAsync(FakeAudiobookshelf server, string title, string author, string? asin, int? lengthInMinutes = null)
		=> AudiobookshelfApiService.BookExistsAsync(server.Url, "test-token", "test-library-id", title, author, asin, lengthInMinutes);

	private sealed record FakeItem(string Title, string Author, string? Subtitle = null, string? Asin = null, string? FileName = null, double? DurationSeconds = null);

	/// <summary>
	/// Answers the library search the way Audiobookshelf does: the query has to be a substring of the
	/// title, the subtitle or the ASIN. The response has the shape of a real one, with libraryFiles
	/// beside media rather than inside it.
	/// </summary>
	private sealed class FakeAudiobookshelf : IDisposable
	{
		private readonly HttpListener listener;
		private readonly FakeItem[] items;

		public string Url { get; }
		public List<string> Queries { get; } = [];

		private FakeAudiobookshelf(HttpListener listener, string url, FakeItem[] items)
		{
			this.listener = listener;
			this.items = items;
			Url = url;
			_ = Task.Run(ServeAsync);
		}

		public static FakeAudiobookshelf Start(params FakeItem[] items)
		{
			for (var attempt = 0; ; attempt++)
			{
				var url = $"http://127.0.0.1:{FreePort()}";
				var listener = new HttpListener();
				listener.Prefixes.Add(url + "/");
				try
				{
					listener.Start();
					return new FakeAudiobookshelf(listener, url, items);
				}
				catch (HttpListenerException) when (attempt < 5)
				{
					// Another process took the port between the probe and Start; pick again.
				}
			}
		}

		private static int FreePort()
		{
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
			finally { probe.Stop(); }
		}

		private async Task ServeAsync()
		{
			while (listener.IsListening)
			{
				HttpListenerContext context;
				try { context = await listener.GetContextAsync(); }
				catch (Exception) { return; }

				var query = context.Request.QueryString["q"] ?? "";
				lock (Queries)
					Queries.Add(query);

				var body = Encoding.UTF8.GetBytes(Search(query).ToString());
				context.Response.ContentType = "application/json";
				context.Response.ContentLength64 = body.Length;
				await context.Response.OutputStream.WriteAsync(body);
				context.Response.Close();
			}
		}

		private JObject Search(string query)
		{
			var hits = items.Where(i =>
				(i.Asin?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
				|| i.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| (i.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

			return new JObject
			{
				["book"] = new JArray(hits.Select((item, index) => new JObject
				{
					["libraryItem"] = new JObject
					{
						["id"] = $"item-{index}",
						["media"] = new JObject
						{
							["duration"] = item.DurationSeconds,
							["metadata"] = new JObject
							{
								["title"] = item.Title,
								["subtitle"] = item.Subtitle,
								["asin"] = item.Asin,
								["authors"] = new JArray(new JObject { ["id"] = "author-0", ["name"] = item.Author }),
							},
						},
						["libraryFiles"] = new JArray(new JObject
						{
							["metadata"] = new JObject { ["filename"] = item.FileName ?? "book.m4b" },
						}),
					},
				})),
				["authors"] = new JArray(),
				["series"] = new JArray(),
				["tags"] = new JArray(),
				["narrators"] = new JArray(),
			};
		}

		public void Dispose() => listener.Close();
	}
}
