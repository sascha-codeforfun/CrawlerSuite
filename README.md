# CrawlerSuite

Crawls your own site and reports spelling, content-quality and link defects for triage.

> **Principles — these come before any other goal.**
>
> 1. **Better say nothing than something wrong.** When a check is in doubt, it stays silent.
>    A false finding costs more trust than a missed one.
> 2. **Accuracy first.** A faster result that differs is a defect, not an improvement.
> 3. **No finding depends on time, load or machine.** No clock-based limits anywhere in the
>    analysis: the same snapshot gives the same findings on any machine, under any load.
> 4. **Built for small machines:** a couple of cores and a few GB of memory on a shared host.
>    No speed and no amount of memory can be assumed.
> 5. **A performance change ships only with proof of identical output:** the same snapshot
>    gives byte-identical logs before and after, checked on a real run.
> 6. **No runtime tuning:** no memory-manager settings or environment switches; the app works
>    as built.

A site crawler and content-quality analyzer for your **own** website. Point it at a
site, let it download a snapshot, and it runs a battery of checks over the captured
pages, scripts, PDFs and images — then writes the findings to per-topic logs you can
read, filter, or pull into a spreadsheet.

It is built to *find and report*, not to judge or fix: every check states what it
found and where, and leaves the decision to you. CrawlerSuite is the Windows desktop
edition: it runs the crawl and the analysis with live progress, and shows the
findings in the app rather than only in log files. It completes a full pass over a
mid-size site in minutes and ships as a single self-contained executable that needs
nothing installed on the target machine.

---

## Getting started

### Requirements

- **To run:** Windows 10 or 11, 64-bit. Nothing to install: the program is one self-contained
  file with the .NET runtime inside.
- **To build:** the .NET 10 SDK. The build restores two NuGet packages, HtmlAgilityPack and
  WeCantSpell.Hunspell.
- **Dictionaries:** Hunspell `.dic`/`.aff` files for the languages you check, which you supply
  yourself (see *Dictionaries* under Configuration).

### Build

```
dotnet publish CrawlerSuite/CrawlerSuite.csproj -c Release
```

This writes the single `CrawlerSuite.exe` to
`CrawlerSuite/bin/Release/net10.0-windows/win-x64/publish/`. The engine's tests run with
`dotnet test CrawlerSuite.Core.Tests`.

### First start

Put `CrawlerSuite.exe` in a folder of its own. With no configuration yet, the app says so and
points to **Settings** (the gear in the header): unlock the settings, add your site, fill in the
download folder, the page file pattern and the user dictionary's file name, and save — that
creates `crawlersuite.config.json` next to the program. The app also creates a `dictionaries`
folder with a readme on how to add the dictionaries. Then **Start crawl**.

### Running it

- **Self-contained, single file.** The published executable bundles the .NET runtime
  and all dependencies, so it runs on a plain Windows 10 machine with **nothing
  installed** — copy the exe next to your `crawlersuite.config.json` and run.
- **Nothing per user.** Configuration, dictionaries and the application log sit next
  to the executable; nothing is written to the user profile.
- **Talks to your site only.** The app contacts the configured site (through the
  configured proxy, if any) and nothing else — no update checks, no telemetry.
- A full pass over a mid-size site completes in minutes; it has been run end-to-end
  on low-end hardware without trouble.

---

## The app, page by page

The menu follows the order of the work: Crawl, Dictionaries (remove what the rules now
cover), Spell findings (decide each word), Results, Triage. Set apart at its foot, Comparison
inspects two snapshots.

### Crawl (the Run page)

The Run page shows every phase of the run from the start (phases still to come dimmed, phases
this run leaves out marked skipped) and a run card: the running phase's current step with its
progress by file count, and an overview of every step of the run, phase by phase, ticked as it
completes. Steps that feed a Results tab carry that tab's count (amber when there are findings);
once the run is done, a click on the count opens the tab.

**Run clocks** in the window header, from the first run on: **Crawl** (connectivity check,
crawl and extra download), **Analysis** (every phase after it) and **Total** (the two added).
Amber while a run goes, kept afterwards; time spent waiting for your answers counts towards
neither. A replay shows `Crawl —`.

**Re-run latest** (amber, next to Start crawl) replays the scoped site's latest
snapshot without a single question: no connectivity check, a clean sweep first, no
files renamed and no assets promoted. A locked file still asks; an incomplete or
empty snapshot stops the run with a message.

**Copy for AI** (Steps card) copies the steps table, masked at the strictest level,
with each step's time and what it cost the .NET memory manager: time paused for
clean-up, clean-ups by generation, megabytes allocated, the most memory held during the
step, and the most .NET still counted after a clean-up in it (nearer to what the work needs). The header names the collector mode (workstation or server) and the most memory
held in this run, so runs can be compared for performance work. Memory held is sampled by
the app every half second and at each step's end, and starts again with each run; in the
first run after starting the app the header also gives Windows' own peak, as a check. A
minute after a run ends, the app reads once more what it holds while it waits for the next run;
the run overview shows it, and a copy taken after that minute includes it.

**Chips in the run overview.** Amber counts findings — the rows of the step's Results tab;
broken links are red. A neutral chip is a fact, not work: redirects (with their kinds),
the snapshot's size (without the crawler's response headers), sitemap entries (and why
pages are not in it), pages eligible for search (noindex in the page or by header,
canonical to another page — robots.txt is not read) and the share boilerplate pruning
removes (with the pages the template is checked on). Hover or keyboard focus shows a small
overlay; lines that would read 0 are left out. A click opens the step's Results tab (the
sitemap: Output files) once the results are loaded.

The run overview has its own **Copy for AI**: every phase and step with its state and
the count on its chip (the rows of the Results tab it feeds), masked the same way.
The Results page has one too: every tab, through all tab rows, with its count, and the
notices above the tabs. While a run goes on, both run copies turn indigo and their text
starts with "IN PROGRESS": a copy then shows only the steps done so far.

**Work done.** Once the analysis has finished, the run overview opens with what it worked
through: bytes and files the analysis read, HTML documents parsed, texts and words prepared
for the spell check, and dictionary lookups (those the dictionaries answered, and those
answered from the run's memory of words already checked). The Steps table shows the same per
step in its **Volume** column; both copies for AI carry them. Counted, never estimated, and
never read back by the run: findings are the same with or without them.

### Dictionaries page

Between Crawl and Results, the **Dictionaries** page lists the entries of your user
dictionary and the site's dictionary that the app no longer needs, read from the
snapshot the Results page shows — it works straight after starting the app, no run
needed. Three reasons:

- **Not used** — no page of the run needed the entry: the system dictionary knows the
  word, or no page has it (log 15).
- **Redundant** — covered by Known word parts (made only of them, or one of them plus a
  word the system dictionary knows) or by the acronym rule (log 15). Such an entry is never
  asked: the Known word parts accept the word first, and an acronym is not checked at all. An
  acronym-shaped entry counts only when the run never asked for it: the lists ignore case, so
  "ABC" also accepts "abc", which is checked like any word.
- **Not needed** — pages have the word, but a spell check without your dictionaries
  accepts it too (log 35).

Log 15 is written when dictionary maintenance is on (`Mode` `Report` or `Interactive`).
Log 35 takes a second spell check, which a normal run does not do: **Find entries no
longer needed** re-runs the site's latest snapshot (no download, like Re-run latest)
with that second pass and maintenance as `Report` for this run, then opens the page
again. The second pass writes only to a temporary folder outside the snapshot; the
run's findings are the same as without it.

Each entry: **Remove** takes it out of the file, **Pin** marks it `!` so it is never
offered again, **Keep** leaves it for now. An entry in both dictionaries changes in
both. A numbered backup of each file is made before its first change while the app
runs; the user dictionary, shared by every site, asks once before its first change.
Entries pinned or removed since the report are not shown. Chips filter by reason
and dictionary, as every filter chip does: a click shows only those, none lit shows all. **Copy list** copies the rows shown as tab-separated text (entry,
dictionary, reason, done), masked at the strictest level.

### Spell findings page

Below Dictionaries, **Spell findings** lists every unique misspelling of the page text
in the snapshot the Results page shows — one row per word, with its languages and on
how many pages it is — so a word is decided once, however many pages have it:

- **User** — the word is right, for every site: added to the user dictionary.
- **Site** — right for this site (a brand, a regional word): added to its dictionary.
- **Triage** — a typo, or unclear without context: left to be looked at page by page
  on the Triage page, where each page's spelling items follow its content quality.

The dictionary choices take effect on the next run. Triage is remembered only until
the next run starts, so a changed configuration or dictionary never meets an old
decision. Words already in a dictionary — added here or by hand — show as decided.
Additions go at the end of the file, with the same backup and the same one-time
question for the shared user dictionary as on the Dictionaries page. Words found in
scripts are not listed. Chips filter by language: a click shows only the words of that language
(a word in several languages under each), lit chips add up, none lit shows all.
**Find in Triage**, on every row, opens Triage searched for the word: the pages that have it,
for a word that needs its sentence to be judged.

A **hyphenated word** is checked part by part, and its row shows the parts as the check
placed them (log 36): a known word part, a single capital, a dictionary word, a compound
head — and, lit, the parts no dictionary knows. User and Site then add **only the lit
parts** ("Xyzzy" rather than the whole "Brand-Xyzzy-Haus"); a part can be
switched off before adding. Once its parts have entries, the word shows as decided.
The same holds for a word standing before a hyphen, a suspended head ("Brand-Xyzzy- und
…"): its parts count the same way, and any of them may also be a compound head. Whether a
hyphen follows is read at each place the word occurs.

**Known word parts** (Settings → Spelling → Words) are brand and product names: a word made
only of them passes — "Brand-Line", and one entry standing alone, "Brand" — and they count as
known parts of any hyphenated word. Each entry shows what it did in the primary site's last
run: how many words it let pass, on how many pages, and up to three words that need it, with
their languages. An entry that let nothing pass is marked, and so is one whose every word would
pass without it (its parts are single capitals, acronyms, dictionary or English words): not
needed. A word is judged by the languages its text was checked in together, as the check does:
it passes if one of them accepts it. A word that passes the dictionary as a whole does not ask
the entries, so it counts for none of them. An entry can be switched off without deleting it:
it is then not applied, and the next run shows how many of its flagged words it would let pass.

**Acronyms** — words in all capitals up to a set length (Settings → Spelling → Words: *Ignore
acronyms*, on; *Acronym length*, a slider from 1 to 5, default 3; *Plural s after an acronym*,
on) — are not spell-checked, alone or as part of a hyphenated word: no dictionary can tell a
right abbreviation from a mistyped one. They are listed instead in log 37, once each, with the
number of pages and the text around the first occurrence, sorted so that look-alikes ("ABC",
"ACB") sit together — for a person to look over. Nothing in that list is a finding. The run
steps name how many there were. The two script scans (all inline scripts together, log 29, and
the script files, logs 30–33) follow the same setting.

**Undo** takes back a decision made on this page while the app runs: it removes exactly
the entries that click added — for a hyphenated word, those parts; never an entry that was
in the file before — and opens the row again, to be decided anew. A word sent to Triage
comes back the same way. Undo is offered until the next run starts.

**Copy list** copies the rows shown (the lit language chips) as tab-separated text with a
header line — word, languages, pages, done, parts to add — masked at the strictest level.

### Results

**Explanations.** Every content-quality type has an explanation card: what is flagged and
what is not, whether it is a likely WCAG issue, the likely cause, the fix, and links to the
standard (WCAG, Unicode, HTML, W3C internationalisation). A rule the text cites — "UAX #14,
rule LB12a", "success criterion 2.4.4", "Technique H58" — is a link to it in place, in the app
and in the ticket, and is among the card's links too. Results shows it above the type's
table; control characters are explained by their character. The page being decided in triage
shows, above the first item of each kind of defect, the same card as its Results tab. A likely
WCAG issue is named so in the app ("likely WCAG issue" on the folded card and in Results); its text
opens with what it does to a person using assistive technology — "Keyboard users land on something
a screen reader is told isn't there" — and ends with the requirement ("Accessibility requirement:
WCAG 2.2 · 4.1.2 Name, Role, Value (Level A)") and the legal benchmark (European Accessibility Act / BFSG, EN 301 549 and the
WCAG level it requires), with dates only where they are facts. Giving an item the Locale verdict
shows the Language of Parts card (WCAG 3.1.2): an intended other language still needs
lang markup. In triage the cards are folded to one line until opened. An opened card
stays open for that kind on the next page.

### Triage

Defects go upstream page by page, so triage starts from the list of pages with
defects. A three-state switch per defect type — mirroring the Results tabs —
decides what counts: **off** (not triaged), **ride-along** (goes into the ticket
of a page that has other defects, never lists a page — nor an entry shared by several
pages — by itself) and **on**. The switches are a setting (**Settings → Triage**), one set
for every site and saved like any other; Triage shows what it counts and links there. On:
what an editor can fix in the page's own text — quote errors (unmatched, wrong opener or
closer), ligatures, word collisions, a space at a hyphen, split by formatting, both anchor
types, language mismatch, your patterns, and the direction marks, joiners and other
invisible characters — and the template defects that arrive grouped, embedded BOM and
styled headings (one entry per pattern). Ride-along: what no reader sees or what the
template makes — mixed quote kinds and systems, control characters, decomposed text, mixed
normalisation, both line-break glues, bare text, the three accessibility types, malformed
HTML, content before doctype, paste debris and invisible control characters. So typos and visible text
errors are not buried under defects the page's editor cannot influence; every switch
can still be turned on, e.g. for a targeted accessibility pass. Soft hyphens are off.
Bidi controls are always on, and pages with them are named in a banner at the top. The soft-hyphen switch covers soft hyphens from both finding
types (invisible characters in template text, and control characters in title, meta and
text), so off takes every soft hyphen out of triage; Results still lists them.

On a page, every defect gets a verdict — **Ticket**, **Wontfix** (this page only,
with an optional comment behind "Add a comment") or **Locale** where it applies — saved
as you click. Once all
are in, **Ticket** files the page. Ticket numbers are typed in by hand once the
tickets exist upstream; when a filed page gets new defects it reopens, and filing
again offers to add them to the existing ticket or open a new one, listing what
was filed before and what is new.

**Broken links** are triaged too (log 07), and only by a verdict: nothing turns a 404 into a
ticket on its own. An entry is where the fix is made — a stylesheet for its `url()`
references; the broken address itself when the same reference is written on more than one
page (one fix in a template or component, not one ticket per page); otherwise the page that
holds it. Each broken address is one item, badged with its **reach** — who runs into it:
**Every page view**, **When opened** (clicked, tapped or by keyboard), **Only if a script acts**, **Reach undecided**
(an undecided stylesheet reference: it may well be requested), **Never requested** (nothing
on the crawled pages requests it — whether it is still needed is the operator's call),
**Error elsewhere on site** (a canonical, the CMS content list, an alternate version search
engines read) or **Origin unknown** (nothing saved points to it). Addresses that redirecting
addresses point to are named as redirects, not as a shared template. A stylesheet or script
entry without a source is named in the Source column by its file — path and cache buster cut
("water.min.css") — and has its own filter chip, "stylesheet" or "script". Any other entry
without a source shows its host there, in Triage and in Results. The item shows how the address is referenced, the evidence, and how to
see the 404 in a browser where the snapshot alone cannot prove it. One switch per reach: on
where a visitor or a search engine runs into it; ride-along by default for **Only if a script
acts**, **Reach undecided** and **Never requested**, where none may ever do. The ledger key ignores a cache buster in an address (16 or more hex digits between
dots or slashes), so a rebuild that keeps the file keeps the verdict. Broken links have their
own section, after content quality and before spelling, and wait for nothing; the ticket
document lists them in one section with the reach on each item.

**Images** (log 25) are placed by who uses them (log 38): on the one page or stylesheet that
uses the image, or on the image itself when several do (they are listed, each marked shown or
linked) or none was found. On by default: a truncated file, an oversized file or picture where a
page shows it, and location or device data (GPS, serial numbers, the camera owner). Off by
default: tiny pictures of 0 or 1 pixel (mostly counters or spacers) and names of people (often a
credit meant to be there). An image only offered as a link — a high-resolution download — is not
triaged for its size; its metadata still is. Rights, equipment, program and date fields stay in
Results. The key is the finding and the image, so a verdict holds when the pages using it change.

**Search appearance** (log 08): pages that may appear in search — no noindex in the page or by
header, no canonical naming another page — get page items for what their editor fixes in the
page's settings. All ride along by default: search appearance is where an operator goes deeper
once a page is open for something else — on, it lists a large share of a site at once; switch them
on for an SEO pass. The items: title missing, title not following the site's title pattern,
description missing, meta keywords present, title or description likely cut off in a result —
estimated from letter widths, as search engines cut by width in pixels — and the same title or
description as other pages, each page's item naming the others. Where the other page also has the same description and main heading, it is
likely the same page at another address (a subdomain counts), and the item asks to choose which
address stays in search rather than to write new texts. **Title patterns** (Settings › SEO &
links) name what the system adds to every title, e.g. `{title} | Company`: a page whose title
matches none is reported, only the page's own part counts as cut off (the added text may be cut by
design), and a title that is only the added text counts as missing. A page with any of these shows a **search result preview**: address, title and
description as a desktop result would likely show them, on a dim cream like a page of results,
cut with … where they would likely be cut; the ticket shows the same.

**Main heading** (log 08, pages in search): no H1 or an H1 without text, and several H1s with
different texts, are page items riding along, like search appearance. Several H1s with the same text are off by default: a template
often writes the heading once per layout (wide and narrow screens) and hides all but one, which is
right — the crawler reads the HTML, not what each screen width shows, so the card says how to check
it in the browser's developer tools. Turn it on for a site that does not hide the copies.

**Self-links** (log 09) and **canonical issues** (log 16) are page items like content quality,
each under its own switch, on by default: what the page's editor fixes. A self-link item is one
link in the page's content that leads back to the page, with its excerpts and the address marked.
Links in the template are left out — also on a reference page, where the template is kept
for the other checks: a home page's logo leading to the home page is by design.
Canonical issues bring a chain (the canonical names a page with its own canonical), several
canonical tags on one page, a canonical naming an address that redirects (log 03), and a canonical
naming a page kept out of search (noindex) while the page itself is not. A canonical whose target was not found is left to Broken links
(reach "Error elsewhere on site"), so it is decided once; a canonical to another host stays in
Results only, as log 16 marks it. A snapshot without log 09 or 16 keeps their verdicts.

**Forwarding links** (log 40) are links to the site's own server that hand it a full address in
their query (`…?next=https://…`, URL-encoded or not): the server is to forward the visitor there,
and if it forwards to any address it is given, that is an open redirect (CWE-601). They are found
by the value's shape alone — no parameter names — in the saved pages; **the app never requests
them**, and never goes to a site it was not pointed at. They are no self-links, so they are not in
log 09. Always a finding, decided once: one item per page, parameter and set of target hosts,
under switches locked on; a verdict keeps it out of the next triage, and a target host that turns
up later brings it back. A link to another site's address is **security relevant** (first, and
marked in Results, Triage and the ticket); the item writes every target out in full and says what
to check by hand — that the forwarding accepts only its intended destinations.

**One defect repeated on several pages is one entry.** When the same content-quality finding —
same type, same detail, same context, numbers aside — stands on two or more pages, it is taken
off those pages and listed once, after the pages, under the filter chip "shared across pages":
one card, one verdict, one ticket of its own, for whoever owns the template, component or fragment
it comes from. The item names the first ten pages and counts the rest; the ticket lists them all.
A styled heading is matched by its pattern (tag and heading class), whatever its texts: one entry
per pattern found on two or more pages, each page listed with its texts — so the operator can see
which team to ask, a fixed component heading or titles typed per page. A pattern on one page only
stays with that page. A shared entry is listed when its switch is on; of a ride-along type it
waits, as ride-along findings do on a page. A page added in a later crawl with the same defect joins the entry and its verdict.
Only exact repetition counts: that a class belongs to a component cannot be read from the HTML.

**Order of the list.** Security findings and bidi controls first, then the pages, then shared
entries; within them, pages never ticketed before pages ticketed before — their editor has had a
turn — and among those the one ticketed longest ago first; pages filed in full last; otherwise
most defects first.

**Spelling** is triaged too: one item per flagged word per page (logs 12 and 13), under
its own switch, on by default, so a page with only typos is listed. Words a dictionary
holds by now — added on Spell findings or by hand — are left out. A page shows its
spelling last, below content quality and ride-along, and it unlocks once every
content-quality item of the page has a verdict: a verdict there can change what counts
as a spelling error. **Locale** applies
to spelling too (a word in another language on purpose), and the item shows the other
dictionaries that know the word. Its excerpts show the text as a visitor reads it, a field name such as
"<FloeId>" included, though the check itself leaves such names out. An occurrence outside the page
text says where it stands — "Description: …", "alt of <img>: …", "In the page's script: …" (with
escapes such as \u00e4 decoded) — in the ticket's language, so an editor can find it. Some words can only be judged in their sentence — a company's
registered name, a term of the page's subject — so a spelling item also has **User** and **Site**:
the same choice as on Spell findings, through the same code (the same backup, the same one-time
question before the shared user dictionary). The word is then right everywhere: every page's item
of it is set aside and no longer counts, the item says which dictionary holds it, and **Undo**
takes it back until the next run starts; Spell findings shows the word as decided too. A snapshot without the spell check's logs never removes
spelling verdicts: not checked is not fixed.

**Finding pages by a word.** The search box above the list finds a page by its address (the
text anywhere in it) or by a word it has as a spelling finding — the whole word, ignoring case,
so "frost" finds the pages flagging "Frost", not those flagging "Frosty". The line above the
list says how many of the pages shown have the word, and how many more the lit chips hide.
A spelling item names how many **other pages** have its word; a click goes back to the list,
searched for it, so a word is judged with all its pages in view. A word in a dictionary is no
finding any more and finds nothing.

A space at a hyphen that belongs to a name is kept as a proper name from the finding itself:
the name is shown as the finding has it ("Xyzzy- und Hausverband"), the word next to the two at
the hyphen as a chip to leave out when it belongs to the sentence, and **Add to proper names**
writes it exactly so into the settings — nothing is typed; its acronym can be added there.
Every finding on the page with that name then shows it as kept; it is gone on the next run.
Undo removes only what the click added; a name that was already listed is shown as kept, with
no Undo. Not while a run is going, nor while Settings holds changes not saved.
The verdict buttons are not touched.

In excerpts, the offending character is red; the word it belongs to — or, for a quote,
the quoted phrase — gets a soft highlighter background, so the eye finds the word first.
The same marks appear in the ticket document.

The ledger records dates only, never a time of day: a verdict's time would record how
someone works, and nothing needs it.

The ledger is one `triage-ledger.json` per site, in the site folder next to its
snapshots. Each newer snapshot is matched against it: what is gone is gone —
verdicts of findings no longer found are removed, drafts included; before anything is
removed, the ledger as it was is copied to `triage-ledger.json.1` (the newest five copies are
kept). An older snapshot is shown read-only, and so is a snapshot without the content-quality
logs (10 and 22) — a crawl only, a stopped analysis, content quality off — since "no finding"
there means "not looked".

**Every occurrence marked.** Identical findings on a page are one item in triage, with one
excerpt line per occurrence; each line has its spot marked. The page address in the page view
and in the bidi banner has its copy button right after it, for when the app may not open links.
In the switch summary (and its Copy for AI line), two switches with the same name are told apart
by their family ("Template defects › Control characters").

**Finding a page.** The box above the page list shows the pages whose address contains the
text (ignoring case), together with the lit chips. It searches once typing pauses, or at once
on Enter; Esc clears it.

**Copy for AI on Triage** copies the structure of what triage shows — switches, list counts,
chips, and for an open page each item's kind, verdict and state — but no page address, finding
text, excerpt, source value or ticket number.

**Triage filters.** Chips above the page list filter it: one per page source (coloured as in
the table, with "No source" for pages the content list does not know), and four decision
chips — Open (nothing decided), Edited (decided in part or in full, not ticketed), Ticketed,
and Changed since ticketed (ticketed before, with new findings to decide or decided but not
ticketed again). Each chip counts its pages. Like every filter chip in the app, a click lights a
chip and shows only its pages, another click puts it out; lit chips of a group add up, a group
with none lit shows everything, and while one is lit the others are dimmed. Triage starts with
none lit, so ticketed pages are listed too — what is already reported stays in sight until you
light, say, Open.
Pages with bidi controls are always listed.

#### Ticket document

Once every item of a page has a verdict, "Ticket document…" makes a
document for the recipients, what to change first: the page and its CMS link (clickable in the
saved PDF and XPS, also where a long address wraps) with source, snapshot and date on one line; an
"at a glance" table of each kind of defect with its count (likely WCAG issues and security marked,
items new since the last ticket counted); then each family under a tinted band with its name and
count, room above it so an unrelated topic is seen to start, each kind with its gist and fix in two lines and
every Ticket item with its context marked as in the app — spelling as each word in bold with its place, the
search result preview with the search items; Locale items get the Language of Parts gist. The
explanation cards in full, with their references (and for a likely WCAG issue the requirement and
the legal benchmark), close the document under "Background", on a page of its own. The preview has its own tool bar in
the app's theme: zoom, fit width, a whole page, two pages. For a page
ticketed before, items new since the last ticket are marked. **Ticket language** (Settings ›
Triage): English or Deutsch. The document — its texts, the cards, the names of defects and every
sentence the app writes about an item — is then in that language; page text, addresses, markup and
the content of the logs stay as found, and the app's screens stay English. In German the WCAG
criteria carry their authorized German titles (WCAG 2.0 translation, "2.4.2 Seite mit Titel
versehen"); W3C rule and technique names stay English, quoted. A **shared entry** has a layout of
its own, the finding first: its title, an "at a glance" box — what it is, where to fix it (the one
place the pages share), the card's fix, and the scope (pages, sections, hosts) — then what was
found, three pages to open from different sections, where on the site it appears (the largest
sections with their page counts, a folder every page shares left out), the texts found on three
or more pages, the card in full as background, and last every page as a path under its host, only
to show the scale. A preview comes first, showing the laid-out pages directly (following
the screen mask; the saved document shows the real addresses), then Save as PDF or Save as XPS.
The PDF is written by the app itself from the same laid-out pages as the preview (fonts embedded
as subsets with only the tables a PDF draws with, so a symbol from a colour emoji font costs a few
kilobytes, not megabytes; text copyable and searchable, every link one clickable area per line); no printer is
involved. Content it cannot represent stops the export with a message naming it, so no PDF is
ever written wrong; notes on anything not exact are shown after saving (see `PDF-EXPORT.md`). Saving starts in Documents —
never in the snapshot folder — and the app keeps no copy. Made on demand only.

### Comparison page

**Comparison**, at the foot of the menu, compares two snapshots of the scoped site — A, usually the
earlier one, and B — file by file: what really changed between two crawls, such as files a crawl
lost to a dropped connection, a template change, or a script that changed behind the same address.
It reads only: nothing in either snapshot is written and nothing is fetched. Findings are not
compared.

- **Pages as pruned.** A page is compared as its run pruned it — the snapshot's `pruned` folder:
  the boilerplate groups applied (navigation, footer, and the attribute parts below), reference pages
  whole but for their attribute parts. A template change then shows on the reference pages, and every other page stays identical
  unless something on it really changed. A reference page says so: **Reference page** in the list's
  Kind column and as a pill in front of its state on the File tab. Every other file is compared as saved, byte for byte.
  Both snapshots need to have been pruned with the same groups. Each prune leaves a **prune
  stamp** in the snapshot (which groups it ran with), and the page says when A and B were
  pruned differently, with groups other than the ones in effect now, or before stamps existed.
  **Re-prune A and B** prunes the pair's saved pages again with the current groups and compares
  again; **Re-prune all** does it for every snapshot of the site — after a change to the groups,
  so every comparison sees the same rules. A re-prune writes only the snapshot's `pruned` folder
  and its stamp: no analysis, no findings, and never the triage ledger. Snapshots whose crawl did
  not finish are left as they are; a stopped re-prune keeps the old pages.
- **The pair.** Two pickers list the site's snapshots; when the page opens, B is the latest and
  A the one before. ⇄ swaps them. Pickers, Compare and the time the comparison took share one
  row — in a window too narrow for all of it, the buttons move to a line of their own below the
  pickers instead of being cut off, and the status stays one line, shortened with "…" (the whole
  text in its tooltip); dates show the month in cyan and the day in amber, in the pickers and over the two sides
  of a file, so two snapshots are told apart at a glance. How pages are compared is under ⓘ;
  what the comparison found is the card's second line. The same snapshot on both sides is refused, also when reached
  through another spelling of its path or a link; so is one without pruned pages. A later A is
  allowed, with a note: "only in A" then means files that came after B.
- **The set** is every file in A's and B's `download` folder, paired by name; the response-header
  sidecars (`.header`) are left out. **Every difference counts**, blanks and line ends included —
  a whitespace change can be a template change nobody sees — except **cache busters**, the parts of
  an address a site regenerates without the content changing: a time in milliseconds as a file
  name (`…/picture.png/1712345678901.png`, any extension) and a build fingerprint of 16 or more
  hex digits between dots or slashes (`lib.min.<hash>.js`). In a page they are set aside; a file
  saved only in A and one saved only in B whose addresses differ only in a cache buster are paired
  and compared by their bytes — the same bytes count as identical ("saved again"), other bytes
  are listed as differing, with A's address in the note. Pages also have their **per-delivery
  tokens** set aside: values a page carries freshly random on every delivery so that scripts
  cannot fill in its forms — the names of honeypot and guard fields and of the form itself
  (16 letters with randomly scattered case, or 16 hex digits as a name, id or form reference).
  A page that differs only in them counts as identical ("but for per-delivery tokens"). And pages
  compared as pruned have their **blank lines** set aside — lines that are empty or hold only
  spaces and tabs, each with its line end: a boilerplate part the prune removes leaves its
  indentation and line breaks behind, so a page whose rules changed between two snapshots could
  otherwise differ in nothing else ("but for blank lines"). Whitespace within a line still counts,
  and every file that is not a page is still compared byte for byte. The
  comparison alone sets these aside: the triage ledger, the prune and the analysis see
  everything as it is.
- **PDFs written out again.** A generator that writes a PDF anew moves object numbers, the cross-reference table, the subset tags of its fonts
  (`ABCDEF+`) and its stamps (creation and modification date, producer, creator, file ID, XMP),
  so the bytes differ although nothing on the pages did. A PDF whose bytes differ is therefore
  unpacked: every object and stream (page content, fonts, pictures) hashed apart from that
  layout. The same content and page count count as identical ("written out again"); any other
  difference stays listed. The subset tag also stands inside an embedded TrueType or OpenType
  font program (its name table), with checksums that follow from it; these are set aside too,
  the glyphs still count. An encrypted PDF cannot be unpacked and is compared by its bytes;
  PDFs over 32 MB too.
- **The site's own patterns in PDF text.** Some generators print something new on every
  regeneration — a form number that carries the day it was written, a dated line. The code
  cannot know that, so the site says it: **Settings → Comparison → Patterns set aside in PDF
  text**, a list of named regular expressions (run without backtracking: no lookarounds, no
  back-references), off while empty. Each match in a PDF's page content is set aside; a PDF that
  is then the same counts as identical "but for configured patterns", and the summary names each
  pattern with the number of PDFs it took. Its font subsets are then not compared by their bytes:
  a subset holds the glyphs its text uses, so a set-aside digit changes it too. That happens only
  when a pattern matched and every other part — every other line of text — is the same; text a
  PDF stores encoded cannot be matched and still counts. A loose pattern hides real changes, so
  each entry is checked on saving (and at start-up) and should stay narrow.
- **Identical files are counted, not listed.** The list holds the files that differ, are only
  in A or only in B, with the address each was saved from, on the **Files** tab; a file paired
  across a cache buster has a second line with A's address (in full on hover). Chips above it
  filter by kind (Pages, Files) and by state (Differ, Only in A, Only in B): a click lights a chip
  and shows its files, another click puts it out; lit chips of a group add up, and a group with
  none lit shows everything. A third group follows for files: one chip per **file type** (the
  Kind column — svg, jpg, pdf …), the most frequent first; past eight types the rarest share one
  **other** chip, and a type with no file among those the other chips let through is hidden. The
  groups combine (Only in B + svg: the icons only B has); a lit type chip shows no pages, and with
  only Pages lit the type chips are hidden. The state chips count the files the kind and type
  chips let through, the type chips those the kind and state chips let through. The summary gives each side's file
  count and how many files are identical — and how many pages only as pruned (their saved files
  differ in the boilerplate only), how many but for cache busters, and how many files were saved
  again under a new cache buster.
- **One file, side by side**, on the **File** tab, with the whole height. A click on a row selects
  it and readies the tab (its header names the file); a double click or Enter opens it. **‹ Back
  to list**, Esc or the mouse's back button return to the list where it was. A and B stand next to
  each other, line by line, with
  line numbers; changed lines are tinted (A red, B green), and on a changed line each part that
  differs is marked on its own — solid red on A's side, solid green on B's, in white, so what
  changed stands out from the row's tint; words whole, a cache buster never — its blanks, tabs and line ends
  made visible (`·` `→` `␍`). A picture a changed line carries in the source as base64
  (`data:image/…;base64,…` — an icon, a photo, a QR code; the tool does not guess which) is shown
  as a picture under the line, labelled "Embedded in the source as base64" with its type (read
  from the bytes; a wrong label is named), size in pixels and bytes, drawn to fit — never wider
  than its cell, never taller than the list, never enlarged; an SVG shows its text, a type Windows
  cannot draw says why. B's picture says, above it, how it compares with A's: the same bytes, the
  same picture in other bytes, how many pixels differ, or another size. A click on the picture (or
  **Open…**) shows it in a window of its own, at full size with scroll bars or fitted to the
  window, and **Save as…** writes its bytes exactly as the source holds them, to a place you
  choose — nothing re-encoded, nothing written into the snapshot.
  A simple SVG — paths, rectangles, circles, ellipses, lines, polylines and polygons, in groups,
  with fill, stroke, opacity, fill rule and transforms — is drawn too, embedded or as a saved
  `.svg` file (drawn above its text), with WPF's own shapes, no library: a colour the SVG leaves
  open (currentColor, or none given) in the app's text colour, and the label says so. B's drawing
  is compared with A's by drawing both at the same size: "the same drawing in other text", or how
  many pixels differ. An SVG using anything else — text, gradients, filters, masks, clip paths,
  use, embedded images, CSS — is not drawn at all, and the card names what it uses: a preview
  that left parts out would look complete. The base64 text folds to one marked piece while the picture is shown;
  **Show the base64 text** unfolds it, marks and all. Unchanged
  stretches fold to three lines around each change; **All lines** shows them all. A very long
  line is cut around its change. **Next change / Previous change** (F8 / Shift+F8) move through
  the file's changes, **Next file / Previous file** through the list. For a page, **As saved**
  shows the saved page instead of the pruned one. A file that is not text, or is larger than
  16 MB, shows its size and SHA-256 on each side. A **picture** (PNG, JPEG, GIF — its first
  frame —, BMP, TIFF, ICO; WebP when Windows has its codec) also shows A and B next to each other
  on a checkerboard, so transparency shows, with width × height and size under each date:
  fitted to the space, or at its own pixels with **100 %**, both sides scrolling together. A
  picture that cannot be decoded says why — a damaged or cut-short file is a signal in itself.
  The file is read into memory first, so nothing in the snapshot is locked; a picture over
  4096 pixels is decoded scaled down; its checksum stands in short under its size (in full on
  hover). SVG is text and is compared line by line. A **PDF** shows what differs inside it:
  pages on each side, whether its content is the same, how many of its objects and streams
  are the same or only on one side, and which stamps changed — as a table of A and B, dates
  readable, changed rows tinted, XMP dates and IDs included — and below it the parts only one
  side has, with their kind (page content, font, picture, page …), size and dictionary or first
  readable characters. A file only in A or only in B says what the **other snapshot's crawl**
  did with its address: could not download it (and why), got a 404, was redirected, or never
  asked for it. The file's position in the list ("12 of 112") stands between Previous and Next
  file, which grey out at the ends; the address in the heading opens the live page in the
  browser.
  **Show A / B in folder** selects the file in
  Explorer without opening it: a saved page opened in a browser would load its images and
  scripts from the live site.
- **Speed and memory.** Pairs are compared in parallel, as many as Settings → Crawl → Parallel
  work allows, each worker holding one pair; a file is read in blocks, never whole. A comparison
  that a re-run has made stale is dropped: compare again.

---

## What it checks

### Spell check

Multi-language spell-checking is the heart of the tool, and it reaches places most
checkers don't:

- **Page text**, across **as many dictionaries as you care to configure** — tested
  with up to **26 languages** in a single run.
- **Inline JavaScript string literals** — the user-visible strings embedded in a
  page's own `<script>` blocks, which ordinary spell-checkers never see.
- **String literals inside `.js` files** — the same, for external scripts.
- **Per-element language** — each run of text is checked against the language
  declared closest to it (its nearest `lang` attribute, with the page language as the
  floor), so a foreign-language island inside an otherwise-English page is checked
  against the right dictionary instead of drowning in false positives — at virtually
  no added cost. That includes inline elements: `<span lang="en">` (or an `a`, `em`, …) in a
  German paragraph is a run of its own, checked in English; a `lang` repeating the surrounding
  language changes nothing.

Dictionaries are Hunspell `.dic`/`.aff` files. They are **operator-supplied** (none
ship with the app); on a fresh install the app points you at where to put them and
how to wire them up. An optional **foreign-words list** lets you allow researched,
comment-justified words from any script without loosening your main dictionaries.

Every dictionary check runs without a clock: the spell-check library limits some of its
searches by time, and when a limit runs out the verdict depends on how busy the machine is —
valid compounds come out unknown, misspelt ones pass, and two runs of the same snapshot differ.
All four of the library's limits are off. Without them, one part of that search can run for
minutes on a single word: the character-variant table (`MAP`, e → é è ê …) of an .aff, used
when the library tests whether a long compound is a misspelt dictionary word. Its work grows
exponentially with the word's length; a dictionary with a large table took over a minute for one
ten-letter word. The table is therefore left out when an .aff is loaded (the file itself is not
changed); the test still tries every other kind of misspelling, and no word takes more than
milliseconds. The one thing lost: a compound of three or more parts that differs from a
dictionary word only by such a variant now passes. For comparing runs, Debug mode (below) offers
**Test levers**: switch a clock back on, or make checks take turns (per language or for the
whole program), or keep the character variants. They hold for the session only, are never saved, and every run names the
levers it used in its run steps (row "Test levers").

#### Languages and flavours

Each dictionary is listed under the language as pages write it in `lang`: `de`, or a flavour
such as `en-GB` / `en-US`. A page naming a flavour (`lang="en-US"`) is checked with it; a page
naming only the language (`lang="en"`), or none (through the default language), is checked with
the flavour marked **Primary** — one per language that has several. `en-UK`, common in pages,
is read as `en-GB`. The default language, page-language overrides and the script scans' lists
may name a language (meaning its Primary) or a flavour. The run overview names a code that is
no language tag and a language with no or two Primary flavours.

The other checks (quotes, content quality) use the language alone: `en-GB` and `en-US` are
both English there. **Fallback dictionaries for scripts** takes a list, e.g. `en-GB, en-US`: a
script word any of them accepts is not flagged.

A dictionary is read the first time a word is checked in it, not at the start of the run: a run
whose pages are in two languages holds those two while it checks the pages. The script scans
read the languages on their lists, and a check across all languages (the suppression
suggestions, dictionary maintenance) reads the rest. When the analysis ends, every dictionary
is let go. The run steps show the configured languages under "Languages" and the ones the run
used under "Dictionaries loaded".

A flagged word that another reference dictionary knows — an English flavour, or the default
language — is named with it in log 11 (`word (de) · valid en-GB, en-US`) and gets a pill in
Spell findings. Information only: the word stays flagged, and the decision stays yours (a
loan word used once is a page matter in triage; one used often belongs in the user dictionary).

### Content quality

A battery of checks for the small, easy-to-miss defects that slip into published
HTML and quietly degrade text quality. Each is reported with the file and the
surrounding context. What it looks for:

- **Typographic quotes** — an opener with no closer, a closer with no opener, an
  opener and closer from different quote systems, or a page mixing more than one
  quote system. A lower-confidence "ambiguous" tier is flagged separately for review.
  A quotation opened and closed with the same mark ("“Text“") is one finding naming the wrong
  end by the page language; on a page declared in several languages, by the first of them.
- **Ligature characters** found in visible text.
- **Invisible characters** — control, bidirectional, zero-width characters and stray
  byte-order marks, detected across the whole page (with `<title>`, `<meta>` and
  editor-authored body text each called out specifically).
- **Mixed alphabets (homoglyphs)** — a single word built from more than one script,
  where look-alike letters stand in for each other — e.g. a Latin `A` where the
  Cyrillic `А` was meant. Same glyph, different character, so it silently matches no
  dictionary; reported with the impostor character pinpointed.
- **Confusable punctuation in code** — a non-ASCII look-alike of an ASCII syntax
  character wedged between letters (e.g. a stray full-width or prolonged-sound mark
  inside what should read `padding-top`), reported with the plain-ASCII form it
  should have been.
- **Decomposed & mixed-normalization text** — visible text stored in decomposed
  Unicode form (a base letter plus a separate combining mark) where the composed form
  was meant: visually identical, but byte-fragile for site search, assistive
  technology, AI and anything else that compares without normalizing. Flags both
  fully decomposed text and pages that carry the same word in two different
  encodings. Scripts whose marks have no composed form (e.g. Arabic) are exempt.
- **Bleed** — an inline element butting against bare text with no separator, so two
  words run together into one (e.g. `helloWorld` where `hello World` was meant).
- **Anchor defects** — anchors that close mid-word (a stray letter after the closing
  tag), and adjacent anchors with no separation.
- **Link accessibility** — following the W3C test rules for WCAG: links without an
  accessible name (including image links without alt text), anything still reachable
  with Tab inside `aria-hidden`, and `aria-labelledby` / `aria-describedby` pointing to
  ids that are not on the page. The app names the WCAG criterion each check is based on.
  An element inside `aria-hidden` that would be in the Tab order by what it is, but is
  taken out by its markup or an ancestor's — `tabindex="-1"`, `disabled` on a form
  control or a disabled `<fieldset>` (outside its first legend), `hidden`, `inert`,
  inline `display:none` / `visibility:hidden|collapse` (a nearer `visibility:visible`
  wins), `content-visibility:hidden` on an ancestor, a closed `<details>` (outside its
  summary), `<template>` or `<noscript>` — is no violation: it is recorded as *cleared*
  with the reason as evidence (off in triage; counted apart as "cleared with evidence").
  Clipping, `opacity:0`, zero size or off-screen positioning do not take an element out
  of the Tab order and are still reported.
  The page's stylesheets are read as well — `<link rel="stylesheet">`, `<style>` and
  `@import`, from the snapshot — with the cascade (`!important`, inline style,
  specificity, order) and inheritance. An element they hide is *cleared*, citing file,
  line, column, selector and declaration. Hidden only under a media or container query
  or in a state such as `:hover` or `:focus` is *conditional*. Rules that apply only when
  printing (`@media print`, a print stylesheet) are not read: screen readers and the
  keyboard work on screen, where they never apply. A stylesheet the crawl
  never fetched, or failed to fetch, leaves the verdict *undetermined* (a stylesheet
  answering 404/410 is not there for anyone and is left out). Both ride along in triage;
  the run step "Stylesheets for hidden focus" counts those read and those missing. Selectors that
  cannot be matched without running the page (`:has`, nested rules) are counted, never
  guessed.
  Links without a name are judged against the same stylesheets: one a rule always hides is
  *cleared* (nobody sees it or reaches it with Tab; off in triage, the rule as evidence),
  one hidden only under a query or in a state is *conditional*, one on a page with a
  stylesheet missing from the snapshot *undetermined* — both ride along, as the main check
  does. Where a link's text is missing, the excerpt says so in words at that spot,
  "[Missing link text]" — in a German ticket "[Fehlender Linktext]" — so a reader who does
  not read HTML sees what is wrong.
- **Lost paragraphs** — prose that lost its paragraph markup: text beside real
  paragraphs or headings without a `<p>` of its own, or paragraphs made of `<br><br>`.
  Text alone in a container (labels, widget messages, a single title) is allowed by
  HTML and not reported.
- **Styled headings** — an element that only looks like a heading (`<div class="h2">`):
  assistive technology cannot find it (WCAG 1.3.1). The heading classes are listed in
  `ContentQualityHeadingClasses`; reported for the template team, one row per pattern
  with the pages it is on. Triage and the ticket list every heading of the pattern on the
  page, one per line (a repeated one once, with "×2"), and the class attribute as the page
  writes it ("as found: class=…") when it holds more than the heading class, so the template
  can be searched for it. Not reported: the label of a nested list (`<li><div class="h4">Group</div><ul>…`),
  whose grouping already reaches assistive technology through the list nesting.
  Read from the pruned page like the content checks: a heading in the site's chrome
  (navigation, footer — whatever the boilerplate rules prune) is reported once, on the
  group's reference pages, not on every page that carries it.
- **Configured "unwanted" patterns** — your own list of strings/patterns that
  shouldn't appear in the raw HTML.
- **Malformed HTML** — content before the doctype, and HTML parse errors.
- **CMS template authoring defects** — structural mistakes that come from the
  template, not the content, including unbalanced `{{ }}` binding fences.
- **Language mismatch** — a page whose declared language doesn't match its text.

#### Space at a hyphen

A compound split by a stray space — "Xyzzy- Haus" or "Xyzzy -Haus" —
is reported (SPACE_AFTER_HYPHEN, SPACE_BEFORE_HYPHEN); the spell check cannot see it, because a
suspended head or tail is correct German on its own. A suspended head followed by a joining
word or punctuation ("Haus- und Gartenpflege", "Haus-, Hof- und …", "Ein-/Ausgang") and a
suspended tail after one ("Hauseingänge / -ausgänge") are correct and not reported, nor is a
dash with space on both sides. The joining words are a list per language (Settings → Content
quality → Checks: *Words next to a suspended hyphen*; de and en to start with); the element's
language decides. Code, preformatted text and scripts are skipped. Soft hyphens are invisible
and read as nothing. A name written this way on purpose ("Xyzzy- und Hausverband") can be kept
as a proper name (Settings → Spelling → Words: *Proper names*) exactly as written; it then
counts in every language. The finding names the word next
to the two at the hyphen too ("Xyzzy- und Hausverband"), so Triage can keep the name by a click
(below) instead of it being typed by hand. The check cannot tell a stray space from a joining
word or slash left out by the text alone, so it asks the dictionary of the page's language
(they load before the content checks): when the head takes the end of the next word and makes a
word ("Stadt- Landhaus" — "Stadthaus"), the finding says "/ or a joining word missing" (fix:
"Stadt-/Landhaus", "Stadt- und Landhaus", "Stadt- oder Landhaus"); the same for a tail
("Hauseingänge -ausgänge" — "Hausausgänge"). A number before the hyphen is a stray space. A slash
at a suspended head or tail takes no spaces ("Stadt- / Landhaus" is reported: "Stadt-/Landhaus").
Without a dictionary for the language the finding names both fixes. The excerpt is the page's
HTML around the spot — the block it stands in, with its markup (letters the source writes as
entities, "&uuml;", are matched too).

#### Doubled words

A word written twice in a row inside one block — "Haus Haus", "the the" —
is reported (WORD_DOUBLED), also across inline formatting ("<b>Haus</b> Haus") and a line
break in the source. Punctuation between the two means it is meant ("Haus, Haus"); words with
digits or an underscore ("XYZ_TOKEN") are not judged; an element with a role or aria-live (a
widget, a status message) is read on its own, as screen readers announce it. A term on its own
line repeated after the `<br>` ("Gartenhaus<br> Gartenhaus heißt …") is no doubling but a style
note, *weak punctuation* (PUNCTUATION_WEAK): a colon after the term ("Gartenhaus:") marks it as
the lead-in. It rides along by default. Doublings that are grammar or meant are listed per language (Settings → Content quality
→ Checks: *Doublings that are correct*; for de: der, den, dem, die, das, sie, wer, so, doch,
teils, ha, bla, toi; for en: had, that, ha, blah — from LanguageTool's word-repeat exceptions as
far as a plain list can follow them). A list cannot see the grammar around the pair, so a slip
of a listed word passes too. In German a capital followed by the same word in small letters
("Leben leben") is a noun and its verb and is not reported, except at the start of a sentence.

### Broken links and missing pages

Finds links that lead to **404s** and other dead ends across the crawled site — and,
crucially, reports **which page(s) link to each broken target**, so you know not just
*what* is broken but *where to fix it*. No configuration and nothing for the page to
declare: it walks what's actually there and tells you what's dead. For most site
owners this is the first thing they want, and it needs no setup to get it.

Every broken address comes with **every place that references it**, found after the crawl
in the saved pages and stylesheets — no extra request: a link, an image, a script, a
stylesheet's `url()`, a data attribute, inline data, a redirect or the content list, with the
reference exactly as written. The tab counts addresses; one that only a page's canonical
points to is the canonical's finding. Two explanations the app can prove are named: a link
written without a leading `/` on a page in a folder (it works only from the top level, and
may pass a click test when a script takes over the click), and a stylesheet deployed twice.
A reference in a stylesheet is graded by when a browser requests it — **fires on load**,
**fires on matching devices** (the rule matches under a media or container query on screen
size, orientation or colour scheme: requested on load wherever that holds), **fires in a
state** (`:hover`, `:focus`, `:checked` — a visitor acts; a state under a media query is still
a state), **fires when printed** (`@media print`), **can fire if a script adds the class** (only a script names the class its rule needs — the
snapshot cannot show that it ever adds it), **dead reference** (no crawled page and no script names that class — unused
here; before removing it, check the stylesheet serves nothing beyond the crawl, such as a login
area) or **undecided** (the app cannot judge the rule, e.g. `@font-face` or a selector it
cannot read; the evidence says why). The evidence names the state or condition as written in
the stylesheet. Scripts are not run, so a class built from parts at run time is not
seen. Every reference also has its **reach** (see Triage), and the run overview's chip breaks
the broken addresses down by reach. An address referenced the same way from many pages is one
row in the table; log 07 keeps every reference.

### More checks

Smaller, focused checks that run alongside spelling and content quality:

- **Image asset metadata** — flags images that were published with embedded
  metadata still attached: device, author, copyright and location details that often
  shouldn't ship on a public site, and agency/credit fields that can indicate a
  licensed asset. (Read with the app's own metadata reader — no third-party
  dependency.)
- **PDF quality markers** — reports the metadata and accessibility flags each PDF
  *declares about itself*: title, language, tags, structure tree, alt-text presence,
  and PDF/A and PDF/UA markers. It reports what the file claims, not whether those
  claims actually hold — so its real value is showing you which documents are
  *missing* the markers entirely. It also reads a PDF's **protection** for what it does
  to readers: a password to open locks everyone out — on a public site most likely a
  mistake (`PDF_PASSWORD_TO_OPEN`); a restriction that also forbids extraction for
  accessibility locks a screen reader out (`PDF_ACCESSIBILITY_BLOCKED`, PDF/UA-1 7.16).
  A restriction that leaves accessibility alone is only noted ("restricted"): it protects
  nothing — the file opens for everyone, and honouring it is up to the viewer — so it is
  no gap. Access to a file is a matter for the server, not for a password in the file.
  (Read with the app's own PDF reader — no third-party dependency.)
- **Redirect analysis** — surfaces redirect problems, including circular redirects.
- **Self-linking pages** — pages that link to themselves.
- **Forwarding links** — links that hand the site's own server an address to forward to (a
  possible open redirect), read from the pages without ever requesting them.
- **Canonical declaration issues** — problems with a page's declared canonical URL.
- **Resource bloat** — pages and assets heavier than they should be, against a
  configurable baseline — and for assets bloated by embedded Base64 data (a common
  cause), it decodes and saves the embedded files so you can see what they actually are.
- **Sitemap** — generates a sitemap from the data your pages declare.
- **SEO fields** — reports the SEO data your pages have set. This **reflects what's
  already on the page** — it does not generate, score, or optimize anything; if a
  page declares nothing, there is nothing to report.

…and a few more, each writing to its own log.

---

## Configuration

### The Settings page

Settings live in `crawlersuite.config.json` next to the program — the only
configuration file the app reads; it ships none, so an update never touches yours. The
file is plain data: it holds what differs from the built-in defaults, and the
explanations live in the app. When the app writes it, it writes no comments, objects
indented, and lists compact — a list of values on one line, a list of objects one per
line. A file with comments still loads;
the comments go when the app first saves it, after a numbered backup (`…json.1`). The backup
holds no proxy credentials: who signs in to the proxy is never configuration.

The **Settings** page (the gear in the header) edits the file. Its tabs (Sites, Crawl, Network, CMS, Spelling,
Dictionaries, Content quality, SEO & links, Assets, Triage, Comparison, Debug) list each setting in plain words,
with its key and an explanation. A closed lock means the built-in default applies and the
file does not mention the setting; opening the lock writes the default into the file to
change it, closing it again removes the entry. **Show defaults** lists the locked ones
too. Save runs the same checks as a start and refuses what would not load; entries the
app does not know (mistyped, or no longer used) are named and dropped,
and a value that only repeats its default is asked about one by one.

Each tab has its own **search box**: it shows only the settings whose name, key, help or
value contains the text (ignoring case) — list entries, table cells and chips included — with
the hits marked and, for a hit in the value, the entry it was found in. Matching defaults show
even with Show defaults off. On the Sites tab it filters the sites by name, address and allowed
subdomains. The search applies once typing pauses (Enter applies it at once). Each tab keeps
its own search; Esc or ✕ clears it. **Section headings fold and
unfold** their section (Fold all / Unfold all per tab); a search opens every section with a
match. Searches and folds last while the app runs and are never saved.

Under each field the default says what the app actually does then ("your Windows sign-in",
"the file's first column", "built-in list of 25 entries"); unlocking a built-in list starts
from its entries, so nothing changes until you edit it. Lists and maps with flat entries
are edited as tables with a row per entry; entries with nested parts stay JSON. The
download folder shows the free space of its drive, rated against the crawl download limit
(red below 5×, yellow below 20×, green from there). Parallel work is a slider from All to
the processors this computer offers, with what Windows reports about them. The file-type
policies show a card for the chosen option. A setting with `{tenant}` or
`{productiongroup}` shows what it becomes for each site — or, where placeholders are not
resolved, says that they stay as written.

**Languages by page** lists the installed dictionary languages as checkboxes. A language
without a bundle stays in the file greyed out, and the run checks those pages with the
languages that are there (the Crawl page says which is missing). A path with nothing ticked
is not spell-checked — that is also how pages are left out of the spell check. The
technical XPaths, the attributes not checked and the boilerplate parts show how often each
entry matched in the primary site's last run, and each can be switched off without deleting
it; switched-off entries are still counted, so the next run shows whether one is needed.
The script strings to skip get the same treatment: each shows how often it kept a string from
the check in inline scripts and in script files. Each **download exclusion** shows what it kept
out in the primary site's latest crawl — how many different addresses, how many times, main
crawl and extra download apart; every entry an address contains is counted, also one behind a
broader entry and a switched-off one (what it would keep out). These are counted while
crawling and kept in a file of their own (`crawl-usage.json`) that a re-run keeps with the crawl's
logs, so they stay the numbers of the last crawl. The content list's **paths to
leave out** (Settings › CMS) can be switched off one by one too, and each shows how many rows of
the CMS export it matched, of how many read ("1 of 1,000 rows left out") — counted whenever the export is read, so a re-run
brings them without a request to the site. An entry that did nothing shows an amber card.
The download folder, the page file pattern and the user dictionary have no default: they say
"Required." and have no lock.

The embedded-data scan names its file kinds as chips (Pages, Scripts and Style sheets on
by default; files are typed by their content, so pages from any server technology count as
Pages), and heading classes are h1–h6 checkboxes plus chips for the site's own classes.
**Issue suppressions** are cards: the finding type from a list of plain names (the card
shows the code the file stores), an optional case-sensitive value — empty switches the
whole type off, said in amber — a required comment, an Active switch, and how many findings
on how many pages each rule suppressed in the primary site's last run. **Unwanted patterns**
are cards by shape: words and markers, a placeholder (opener and closer, optionally folding
the findings of another group while it is open), or a template binding that is reported
only when broken. Adding a group starts from a purpose; each card explains in words what
its switches do, counts its findings in the last run, and can be switched off or marked
**security relevant** — those findings come first and are marked in Results, Triage and the
ticket, and Triage warns when they are left out. A site that does not use the CMS content
list switches it off on its card; then it needs no tenant or production group.

Whether a page is out of search is fixed, not a setting: `noindex` or `none` in its
robots meta tag or in the `X-Robots-Tag` response header (header directives aimed at one
crawler aside). Such pages stay out of the sitemap.

### Sites, locking and proxy sign-in

The settings are **locked** at every start. Unlocking them takes a deliberate tick under
a warning: the sites set here decide what the app downloads, stores and opens for
analysis, so only aim it at sites whose content you trust. They stay unlocked for the
session until you lock them again or start a crawl. Site addresses are checked against
the address rules as you type (absolute https or http address, valid host, no
credentials, query or fragment; allowed subdomains as base addresses), with exactly one
primary site.

**Proxy sign-in:** only the proxy's address is configuration. Every crawl
through it signs in with your Windows account and asks nothing. Only when the proxy refuses
that does the app ask for a user (your Windows user filled in) and a password, and again while
the proxy refuses what you typed; Sign in stays off until a password is there. Nothing of it is
stored: a start over keeps it, the end of the crawl drops it, and the next crawl tries your
Windows account again.

### CMS content list

**Pages not online.** An export of the CMS can list pages that are not on the site — not
published yet, or taken offline. Which rows are online is the export's matter, so the app
takes it from the configuration alone: `CmsContentList.OnlineColumn` names the column (as in
the header row after `SkipRows`) and `CmsContentList.OnlinePattern` the value that means
online (`*` matches anything, case ignored). Only matching rows are pages of the site: the crawl
compares and fetches those. The others — any other value, an empty cell too — are never
requested and are listed apart (log 39, Results › Crawl › Not online in the CMS); the crawl's
step row counts them. A live page that links to one is still a broken link, and its item says
the CMS has the page but not as online. Both settings empty (the default): every row is a
page. A column the header does not have turns the filter off and says so in the application
log. The ticket's page source still reads every row, so a page published later keeps it.

**Page source.** With a CMS content list configured (`CmsContentList`, and the source and
mapping columns in `TicketGeneration`), every Results table whose rows name a page starts with
a **Source** column: the page's value from `UrlSourceColumn`, followed by the label of the
first matching `SpecialInfoMappings` entry, exactly as the export and your configuration have
them. The app interprets neither; it only colours the chip by which configured key matched:
indigo when a mapping label matched (that wins), amber when the value equals
`UrlSourceExternalName`, rose when it equals `UrlSourceLocalName`, neutral otherwise. Chips and
copy buttons line up in their column. The copy button next to it copies the CMS editor link (the
ticket's `{CmsLink}`) to paste on a system that can reach the CMS; it is never opened from
here, and it copies the real link whatever the screen mask shows. The triage list and the
page being decided show the same. A site without this information shows no Source column. The
Results Copy for AI counts rows per source, with values and labels replaced by neutral names
("source value 1 (local)", "source value 2 + label 1").

### Download limits, retries and fair use

Three download limits protect the snapshot from something going badly wrong on the
site (all configurable):

- `MaxSingleFileMB` (default 150) — a file larger than this is not stored. The app
  keeps its first 64 KB and its response headers in the snapshot's `oversized`
  folder, so you can tell what it was, and lists it in `34-oversized-files.log`.
- `MaxCrawlDownloadGB` (default 10) — when a crawl downloads more than this, all
  downloads stop and you decide whether to delete the snapshot or delete it and
  crawl again. Raise it if your site really is that big.
- `StallTimeoutSeconds` (default 30) — once data is flowing, a download that
  receives nothing for this long is abandoned. A server that is slow to start
  answering gets 100 s and a retry; that is waiting, not a stall.

**When the server has a bad moment.** A download that fails for a passing reason —
it stalled, got no answer, the connection or TLS handshake failed, or the server said
429 or 5xx — is asked again a few times at once and, if it still fails, is **not lost**:
it waits for the **retry round** at the end of the download phase, which tries every
such download once more after a 30 s pause, four at a time. Only what fails there too
is logged as failed; what succeeds is logged as saved, as if nothing had happened. The
run view counts them under **To retry**, and the step row says how many were
recovered and how many still failed. The **Errors** tile splits what failed for good by kind:
404 (the site's broken links), other 4xx, 5xx and connection; **Downloading** counts only
requests actually under way — one waiting for its turn under the request ceiling is waiting. Pages and assets are treated alike.

And the crawl does not keep firing at a server that is pushing back: when such
failures cluster (8 within 10 s), new requests to that server are **paused** — 30 s
at first, doubling up to 5 minutes while it is still not back. Then one request goes
out alone; once it is answered, requests resume at 2 a second and speed up step by
step to full pace. The run view says while requests are held; the step row says how
often it happened. A pause needs failures from 8 **different** addresses — retries of one
address never add up to one — and only connection-level failures and the answers that ask
to be left alone (429, 502, 503, 504) count: a **500** is that address's own answer, so an
endpoint that always fails cannot hold back a healthy server. A probe goes to an address
that has not failed, if one asks within a moment; if only addresses that failed before are
left, their failure ends the pause instead of lengthening it.

**Fair use.** The crawl is paced so it never hits a server harder than it needs to:

- `MaxRequestsPerSecond` (default 50) — the most requests per second to one server, pages
  and assets together; 0 means no ceiling. One view of a modern page is some 30 to 40
  requests within a second, so 50 is about one and a half page views every second. Above
  100 the settings warn: fine for a site whose infrastructure you know, a strain for a small
  one or for a protection that counts requests.
- `MaxConnectionsPerServer` (default 16) — the most connections open to one server at a
  time. A browser opens 6. Downloads beyond this wait for a free connection, and slower
  answers then mean fewer requests per second, as in a browser.
- `HighestHttpVersion` (default 1.1) — the newest protocol offered; each server is spoken
  to in the best version it agrees to, up to this. 1.1 gives every request a connection of
  its own while it runs, so a failure, a stall and every measurement belong to that request
  alone; 2 and 3 share a connection among many requests, as browsers do where the server
  offers them (3 needs Windows 11 or Server 2022 and later, otherwise 2 or 1.1 is used).
  The step row says which version the servers answered in, and whether one offers HTTP/3.

The crawl sends **no User-Agent** and does **not follow robots.txt**, on purpose: it is to
get what a visitor's browser gets, and a site may answer a named crawler with a variant made
for it. A `Crawl-delay` in robots.txt is reported at the start of the crawl, not applied.
Addresses with an unfilled template placeholder (`{{name}}`), as scripts carry them, are
not fetched: a template is never a real address.

### Dictionaries

Spell-check needs Hunspell `.dic`/`.aff` files, which you supply yourself (a good
starting point is the [LibreOffice dictionaries](https://github.com/LibreOffice/dictionaries)
collection). On first run the app creates a `dictionaries` folder with a readme
explaining exactly how to add and configure them.

**User or site?** A word that is right on every site you crawl — your organisation's name, a
product, a term of your field — belongs in the **user dictionary**, one file shared by all
sites. A word that is right on one site only — a place, a local offer, a name only that site
uses — belongs in that **site's dictionary**, a file named after the site's folder in the
`dictionaries` folder (`<site folder>.dic`), which applies nowhere else. Keeping site words out of the user dictionary keeps a word that is right on one
site from hiding a typo on another. With a single site the two work alike. Both hold single
words in Hunspell's format and count in every language a page is checked in; names of several
words go into *Proper names* (below).

**Proper names** of several words, such as an institution's name, go into the settings instead
(Spelling → Words: *Proper names*), a table with the name and, if it has one, its acronym.
A word of a name passes the spell check, and a space at a hyphen in it passes the hyphen check,
only where the whole name stands in the same text, so a part does not pass everywhere else; the
acronym passes anywhere with its exact capitals, also when it is longer than the acronyms left
unchecked. A row with no name, an acronym that is not one word of capitals, or a name or
acronym twice is marked at the row. In the file the list reads
`{ "Name": "Xyzzy- und Hausverband", "Acronym": "XHV" }`; a misspelt field there stops the load.

### Boilerplate attribute parts

**Attribute parts** of a boilerplate group (Settings → Spelling → Boilerplate groups, type
*attribute*) take an attribute the template writes with a value that changes on every delivery —
a salt, a nonce, a monitoring id: `@data-x` on every element, `script/@data-x` only on `<script>`.
It is removed on every page, the group's reference pages included (there the rest of the
template stays whole, and a value that changes on every delivery has nothing to check); the
element stays, only the attribute goes, so the spell check does not see its value either. Attributes
the analysis reads — language, classes and ids, addresses, the texts it checks, `aria-*`, script
handlers — are refused. An *xpath* part that selects attributes is refused too: it would remove
their elements. A *class* part shaped like an attribute (`script/@data-x`) or an XPath (`//nav`)
says which type it needs.

### Screen mask

For screenshots and shared screens, the **Screen mask** selector in the banner hides what
identifies your site — in one click, on every page and dialog, including a run in progress:

| Level | A URL shows as |
| ----- | -------------- |
| Clear | `https://www.example.com/en/products/widget.html` |
| Host | `https://•••••/en/products/widget.html` |
| Host + path | `https://•••••/•••/•••/widget.html` |
| Scheme only | `https://•••••` |

On Results and Triage, an address on the snapshot's own root (scheme and host, the one most of
its saved files share) is shown from its path — `/en/products/widget.html`; addresses on any
other host stay whole. The heading names the root — in brackets when the site's name does not
already show it (a snapshot opened from a folder). The tooltip, open and copy always use the
whole address. The explanation card above a Results table starts folded to its first sentence;
the tab rows start open and fold with their chevron to one line naming the tab on screen. Folded
or open, each choice holds until the app closes.

At every level other than Clear, site names show as "Primary site", "Site 2", …, and
configured folders, host names, IP addresses, each site's CMS tenant and production group, and
the proxy account are hidden. On Settings, an edit box whose value the mask hides shows the masked text
and cannot be edited, focused or copied until the mask is off — editing it would show it. The mask changes only what
the window shows — logs and output files are written in full — and it is not stored: the app
always starts in Clear.

---

## What it writes

Each site gets a folder of its own under the download folder, holding its timestamped
snapshots, its triage ledger and its DNS history (its site dictionary is in the `dictionaries`
folder). A snapshot holds the
saved files, the per-topic logs and the files the steps write; **Output files** in Results lists
every file with its format.

- Findings are written to per-topic log files in the snapshot folder, designed to be
  read directly or imported into a spreadsheet. A cell taken from the site that starts with
  `=`, `+`, `-` or `@` gets a leading apostrophe, so a spreadsheet shows it as text, not a
  formula; the app's own screens strip it again.
- **Saved names are the app's.** A page or linked file lands as `.unverified` until the app has
  identified it; an asset keeps its ending only when assets have it (images, styles, scripts,
  fonts, PDF, media, data, server handlers such as `.axd` or `.ashx`) — any other lands as
  `.asset.unverified`. Such an asset the server calls a stylesheet or a script, with plain text
  in it, is settled as `.css` or `.js` after the crawl and analysed like any other. Characters
  Windows treats specially become `_`, and long names are cut keeping their ending.
- **Where the site's names pointed.** Every crawl records the addresses of the site's hosts
  (`dns.json` in the snapshot, kept by a re-run): looked up at the start and, without a proxy,
  where the connections went. The site folder keeps every address of every crawl
  (`dns-history.json`, next to the triage ledger). An address a host never had is reported in
  the run's DNS step and the application log, one of a class the host never had (public before,
  private now) as a warning; an address no website has — loopback, link-local, "this host" — is
  a warning on every crawl, the first one included. Information, nothing is refused (an intranet
  site resolves privately by design). Results › Crawl › DNS lists every address seen, with its class and the
  snapshots it was first and last seen in.

**connectivity.log** in the site folder exists only while the connection fails: every
failed check appends its details (user, machine, addresses, proxy, error — what a proxy
problem needs), and the first successful check deletes the file.

---

## Debug mode: archive and compare every run

For development and for chasing a result that changes when it should not. Switch on
**Debug mode** (Settings → Debug). After every completed run:

- **Archive.** The run's output files are copied to `debug\<site folder>\<snapshot>\<version>-<number>`
  next to the program — every log and CSV, `settings-usage.json`, `sitemap.xml` — with the run
  overview and steps (masked, as copied for AI) and a `run-info.txt`: version, test levers,
  checksums of the configuration and of the user and site dictionaries after the run, and
  whether it was the first run since the app started. The crawled pages are not copied. The
  number counts the runs of a snapshot; names carry no time.
- **Compare.** The run is compared with the previous archived run of the same snapshot, file
  by file. The run steps say which files are identical and which differ, by file only; the
  differing lines are in `compare.txt` in the run's folder. Lines are compared as a multiset
  (order ignored; a line that occurs more often in one run counts as often as it is extra).
  The time line of log 35 and the time in `prune-stamp.json` are left out. Each archived run
  keeps a list of its files with size and SHA-256 (`files.sha256`); two runs are compared by
  their lists, and only files whose hash differs are opened.
- **Baseline.** Once a run is known to be right, **Mark as baseline** on the Run page (Debug
  mode only) makes it the snapshot's baseline: every later run is compared with it as well
  (`compare-baseline.txt`). Marking another run replaces it.
- **Test levers** (Settings → Debug) apply only in Debug mode; outside it every run checks
  with the defaults.
- **Memory manager** (with the test levers): the .NET collector — server with DATAS (the
  default), workstation, or server without DATAS — a "conserve memory" level from 0 to 9, and
  a memory cap (none, 2, 3 or 4 GB) that makes .NET behave as on a machine with that much
  memory, to check what suits a machine. .NET fixes these when the app starts, so **Restart with these**
  closes the window (with its usual questions) and starts the app again with the choice, for
  that one start only: nothing is written to Windows, and a normal start uses the defaults.
  The run-steps header names what each run ran with ("Memory manager: …").
- **Ticket sweep** (Triage, Debug mode only). Every page on the Triage list and every shared
  entry is made into a ticket with all its items as Ticket — decided on a copy in memory: the
  triage ledger, its file and its backups are not touched, and earlier verdicts and ticket
  numbers are not used. Each ticket is written as PDF, the same way as Save as PDF, with its text
  next to it (`.txt`), into `debug\<site folder>\<snapshot>\ticket-sweeps\<version>-<number>`
  (apart from the runs, never counted as one). `sweep.md` lists the checks by kind, one ticket
  per shape (the kinds of findings on it) to read first, then every ticket; `sweep.tsv` is the
  same as a table, and `sweep-compare.md` says what changed against the sweep before. The
  checks are cheap and meant to point the reading: a PDF above 400 KB, font tables a PDF does
  not use, empty brackets or quotes in the text, a spelling excerpt that is in neither the page's
  text nor one of its attribute values, quotation-mark advice against the page's own quotes, and the PDF writer's notes.
  Cancel stops after the ticket in hand; what was written until then is listed.

The app never deletes anything in the debug folder; the Debug tab shows its size.

---

## Project documents

- **[DECISIONS.md](DECISIONS.md)** — the design choices and why they were made, including the
  alternatives that were ruled out and the trade-offs that were accepted.
- **[SECURITY.md](SECURITY.md)** — the threat model, what the app guards against and what it
  leaves to the operator, and how to report a vulnerability.
- **[PDF-EXPORT.md](PDF-EXPORT.md)** — how the app writes ticket PDFs itself, what they contain,
  and their limits.

---

## License

CrawlerSuite is released under the **MIT License** (see `LICENSE.txt`). It bundles a small
number of third-party components; their licences and notices are in the
`CrawlerSuite.Core/licenses` folder and summarised in `THIRD-PARTY-NOTICES.txt` there. Dictionaries are not distributed
with the app and carry their own licences.
