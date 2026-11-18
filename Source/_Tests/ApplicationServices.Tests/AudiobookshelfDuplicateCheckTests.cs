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
	// Libation: title, author, ASIN. Server: title, author, ASIN.
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", "Something Else", "Someone Else", "B0TEST0001", AudiobookshelfApiService.DuplicateMatch.Asin)]
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", "Dune", "Frank Herbert", "B0TEST0002", AudiobookshelfApiService.DuplicateMatch.None)]
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", "Dune", "Frank Herbert", null, AudiobookshelfApiService.DuplicateMatch.TitleAndAuthor)]
	[DataRow("Dune", "Frank Herbert", null, "Dune", "Frank Herbert", "B0TEST0002", AudiobookshelfApiService.DuplicateMatch.TitleAndAuthor)]
	[DataRow("Dune", "Frank Herbert", null, "Dune", "Frank Herbert", null, AudiobookshelfApiService.DuplicateMatch.TitleAndAuthor)]
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", "Dune", null, null, AudiobookshelfApiService.DuplicateMatch.TitleAndAuthor)]
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", "Dune", "Someone Else", null, AudiobookshelfApiService.DuplicateMatch.None)]
	[DataRow("Dune", "Frank Herbert", "B0TEST0001", "Dune Messiah", "Frank Herbert", null, AudiobookshelfApiService.DuplicateMatch.None)]
	public void ClassifyCandidate_weighs_asin_before_title_and_author(
		string libationTitle, string libationAuthor, string? libationAsin,
		string absTitle, string? absAuthor, string? absAsin,
		object expected)
	{
		var match = AudiobookshelfApiService.ClassifyCandidate(
			libationTitle, libationAuthor, libationAsin,
			absTitle, absTitle, absAuthor is null ? [] : [absAuthor], absAsin);

		Assert.AreEqual((AudiobookshelfApiService.DuplicateMatch)expected, match);
	}

	private static Task<bool> BookExistsAsync(FakeAudiobookshelf server, string title, string author, string? asin)
		=> AudiobookshelfApiService.BookExistsAsync(server.Url, "test-token", "test-library-id", title, author, asin);

	private sealed record FakeItem(string Title, string Author, string? Subtitle = null, string? Asin = null, string? FileName = null);

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
