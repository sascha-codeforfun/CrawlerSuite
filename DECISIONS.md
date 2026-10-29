# Decisions — CrawlerSuite

The design choices and the reasons behind them, including the alternatives that were ruled out
and the trade-offs that were accepted. For a reviewer who wants to know why the app works the way
it does. Generic by rule: no site data, names, addresses or figures from real runs.

---

## Keeping the app generic

- Nothing from the sites the app is run on goes into code, comments, tests, fixtures or docs:
  no words, class names, addresses, CMS values or real figures. Fixtures are invented
  (`site.test`, water / ice / vapour).
- No behaviour tuned to one site's quirks. A rule prompted by something seen on a site is stated
  as a general rule that would hold for any site.
- What only a site can know is configuration, never code: which CMS column says a page is online
  and with which value, the patterns a site prints into its PDFs, the text a site adds to every
  title, the classes it uses for headings.

## Stack and dependencies

- WPF, C#, .NET 10, one self-contained file with native libraries included; dark theme
  throughout, title bar included.
- Only two packages: HtmlAgilityPack and WeCantSpell.Hunspell. WeCantSpell is never modified.
  Anything else is weighed before it is added.
- **No browser engine.** A rendered preview of changed page parts in the Comparison would need
  one. WPF's built-in `WebBrowser` is Internet Explorer 11 and renders current layouts (flexbox,
  grid) wrongly — a wrong rendering misleads a comparison. WebView2 is a browser inside the app: a
  large, constantly attacked surface fed with pages from the web, a package and a runtime outside
  the single file, and a lasting duty to follow its security updates and keep its sandboxing and
  request blocking right. Not to be reopened without weighing that burden first. The Comparison
  shows changes as marked text, embedded pictures and simple SVG drawings instead.

## Network and security

- **The app goes only where it was pointed.** The admission gate admits http(s) addresses of the
  configured site and its allowed subdomains, no IP literals. Nothing follows a site's lead to
  anywhere else:
  - The connectivity check asks only whether the configured address answers; it follows no
    redirect, and any 3xx counts as reachable. Where a redirect leads is the crawl's to decide,
    through the gate.
  - **Forwarding links are read, never followed.** A link to the site's own server with a full
    address in its query may be an open redirect. Knowing for sure would need a request, and the
    server's answer points at another site. So such links are found in the saved pages by the
    value's shape (never by parameter names, so nothing of a site is in the code), are always a
    finding, decided once in triage, and the check is left to a person. A link that forwards
    elsewhere is no self-link.
- **Proxy credentials are never stored.** The proxy's address is configuration; who signs in is
  not. Every crawl signs in with the Windows account first and asks nothing. Only when the proxy
  refuses (407) is the operator asked for a user — prefilled with the Windows user name; an app
  many people use must not keep one person's name — and a password. An empty password is no
  sign-in, so Sign in stays off without one. Any other failure asks nothing: a password would not
  help. What was typed lives in the run and is dropped when it ends: a new crawl tries Windows
  again, a start over within the run keeps it (asking again there is ceremony without gain). The
  password is not overwritten in memory: the password box holds it as .NET protects it, and a
  managed string cannot be wiped reliably. An unattended run uses the Windows sign-in only.
- **An old `ProxyPassword` in a configuration file is ignored, not removed.** It is not read, and
  the next save from Settings drops it as an unknown key.
- **No Mark of the Web on saved files.** Writing a `Zone.Identifier` stream on every download
  would make SmartScreen and Office Protected View step in when a file is opened. Ruled out:
  - It guards opening a file, which the app never does (crawled files are only revealed in
    Explorer), and it does not help where the real risk is: Explorer reads `.url`, `.library-ms`
    and similar files by itself when it shows a folder, mark or no mark.
  - Security tools watch alternate data streams closely: hiding data in them is a known attacker
    technique (MITRE ATT&CK T1564.004), and touching this very mark is how malware dodges
    SmartScreen. An unsigned app creating thousands of streams per run is the pattern an endpoint
    protection may flag or block.
  - It works on NTFS only, costs a write per file, and cannot be tested off Windows.

  The cause is dealt with where it lies instead: the site does not choose a saved file's ending.
  Linked files land as `.unverified` until the settle step identifies them; an asset keeps its
  ending only when assets have it, any other lands as `.asset.unverified`. Styles and scripts in
  quarantine are settled automatically when the server names a stylesheet or a script and the
  bytes are plain text — the ending then comes from the app's own two-item list; images only after
  the operator agrees, because their signals can disagree.
- **DNS: recorded, not refused.** Refusing a site name that resolves to a private or loopback
  address was ruled out: the app's claim is "point it at your site", and an intranet site or split
  DNS resolves privately by design — refusing would break legitimate use without stopping someone
  who points the app at the wrong site. What has value is knowing when it changes: a name that
  pointed to one address and now points to another is worth a look. So every crawl records each
  allowed host's addresses (looked up, and without a proxy also where the connections went) in
  `dns.json`, and the site folder keeps every address of every crawl in `dns-history.json`, next
  to the triage ledger and apart from it: one file to compare against, and deleting a snapshot
  does not erase what it saw. An address is new only when the host never had it, so a CDN's
  rotating pool settles after a few crawls; one of a class the host never had is a warning.
  Loopback, link-local and "this host" are a warning on every crawl, first record included: no
  website has them. Private addresses are not: an intranet site is private by design. Through a
  proxy only the lookup on this machine is recorded; the proxy resolves on its own.
- **The screen mask also hides four-part numbers.** IP addresses are masked at every level but
  Clear, and a four-part version in a PDF's or image's metadata ("15.0.4.15") is masked with them.
  Erring toward hiding is what a masked screenshot is for; at Clear nothing is masked, and logs
  never are.
- **Matches are walked, not collected.** A regex match collection keeps every match it produced,
  and the number of matches in a file is the file's choice. Every match over site content is
  walked one at a time, so a crafted file cannot exhaust memory that way.

## Crawl

- **The crawl gets what a visitor's browser gets.** Hence:
  - **No User-Agent is sent**, on purpose: a site answers a named crawler with a variant made
    for it (a bot page, another layout, a consent wall). That an empty User-Agent is itself a
    signature some protections throttle is the accepted price; the gate and the retry round
    deal with it.
  - **robots.txt is not followed**, neither its rules nor its `Crawl-delay`; the Crawl-delay is
    only reported at the start of a crawl. A browser does not read robots.txt, and the content
    and spelling checks must see what visitors see.
- **Fair use by sensible defaults and a warning, not by locks or records:** someone who wants to
  hammer a site is not stopped by a checkbox, and no one else needs one. Defaults: at most 50
  requests per second per server (one view of a modern page is 30 to 40 requests within a
  second), 16 connections per server (a browser opens 6), with a warning in the settings above
  100 per second. The connection cap paces the crawl the way a browser's does: slower answers
  mean fewer requests per second without any extra logic.
- **HTTP/1.1 by default**, HTTP/2 and 3 on request (`HighestHttpVersion`), each server spoken to
  in the best version it agrees to up to that. 1.1 gives every request a connection of its own
  while it runs, so a failure, a stall and every measurement belong to that request alone —
  what a diagnosing crawler needs; 2 and 3 share a connection among many requests.
- **A 500 is an address's own answer, not pushback**; only 429, 502, 503, 504 and
  connection-level failures from 8 different addresses within 10 s pause a server.
- **Online or not is the export's word, set in the configuration.** The app cannot know a CMS's
  columns or states, and must not: a column name or a status value in the code would tie it to one
  site. So the rule is only "the value in the configured column matches the configured pattern";
  everything else — which column, which value, that an empty cell is not online — follows from the
  configuration. Rows not online are kept apart (log 39) rather than dropped: a live link to one
  stays a broken link, and its item can say why the page is missing.
- **An exclusion's use is counted where the crawl decides,** after the host check, for every
  address it contains — also when an earlier entry already rejected the address (otherwise an
  entry behind a broader one looks unused while still needed) and when it is switched off (what
  it would keep out). Counted while crawling, the numbers belong to the crawl: they have a file of
  their own (`crawl-usage.json`), kept with the crawl's logs, and a replay leaves them alone.
- **Paths to leave out are counted from the export, not from the site.** They act only while the
  CMS export is read, so every read counts them and a re-run, which requests nothing from the
  site, brings them up to date.

## Performance

- **Memory over speed.** The machines the app runs on have limited memory, so nothing trades
  memory for speed: parsed pages are not kept between steps. Speed comes from allocating less.
- **Memory manager: server collector with DATAS** (set in the app's project file). The
  workstation collector stops every thread and cleans up on one core; with many threads
  allocating it cleans up so often that short-lived memory survives into the older generations
  and stays until a rare full clean-up — the peak was mostly memory no longer used. Classic server
  gives every core a heap and an allowance from the start, which costs memory. DATAS starts with
  one heap and grows only when clean-up costs too much time: measured on a replay, it held about
  half the peak of workstation, with a fraction of its pauses and identical findings. The test
  lever in Settings → Debug restarts the app with another setup for comparison on any machine.
- **If memory gets tight,** two configuration levers remain, both costing findings: the JS-file
  scan off (it sets the run's peak; loses the spelling of external .js files), or fewer
  dictionary languages (the loaded dictionaries are by far the largest part of what a run needs).
- **Each dictionary loads when first needed and all are let go after the run.** The set knows
  every configured language from the start and reads a dictionary when a word is first checked
  in it; a check across all languages loads the rest. The first dictionary still loads at the
  start, alone: it validates the shared user, site and foreign dictionaries, so a bad shared file
  halts the run there. No collection is forced after the release (principle 6): the memory
  manager gives it back on its own schedule.
- **Memory held is measured per run by the app itself.** Windows keeps one peak per process and
  it cannot be reset, so a later run in the same process would show an earlier run's peak. The
  app samples the memory it holds (every half second and at each step's end) and keeps the
  highest value per step and per run.
- **Skip what can never be a finding before the dictionary, not after.** A .js string of one
  word is dropped before any lookup: the prose gate would demote it if unknown, and a known word
  is no finding. Whatever shortcut is taken, the findings must stay identical.
- **The dictionary-check count varies between identical runs by design.** Threads share one
  cache, and which thread asks the dictionary first is a race. Making the count stable would need
  coordination that costs time; the findings are identical either way.
- **Runs compare by their file lists.** Debug mode's compare of two runs opens a file only when
  its size and hash differ, so identical files are known without reading them; a list that no
  longer matches the files' sizes is worked out again, so a run edited by hand is never trusted
  blindly.

## Display

- **Addresses on the snapshot's own root are shown from their path.** The root is the scheme and
  host most saved files share, worked out from the snapshot, not from a site name, which a
  snapshot opened from a folder does not have. Another host — a subdomain included — stays whole,
  because that difference matters. The heading names the root (in brackets only where the site's
  name does not already show it); tooltip, open and copy use the whole address.
- **Room for the findings.** Explanations fold (the card to its first sentence); a column that
  would be empty on every row is not shown.
- **Notices above the Results tabs are for an incomplete snapshot only** (files not stored, a
  crawl that did not finish). Findings belong in their tab; a number shown twice drifts apart.
- **One rule for every filter chip.** A click lights a chip and shows only its items; lit chips
  of a group add up; none lit shows everything; the others are dimmed while one is lit; every row
  starts with none lit — so the same click does the same thing on every page. Triage therefore
  starts with ticketed pages listed: what is already reported stays in sight. Settings' value
  chips (file kinds, heading classes) are choices, not filters.
- **One name: reference page.** The page a boilerplate group's parts are checked on and kept whole
  is the reference copy of the template, checked there once and compared there whole; the same
  name is used in every screen, document and identifier. The config key `PagesToCheckBoiler`
  keeps its name, so configurations keep working.
- **Simple SVGs are drawn, without a library.** Icons are mostly paths in a view box; WPF draws
  those with its own geometry once SVG's looser path syntax is rewritten. Whatever goes beyond the
  simple subset leaves the whole SVG undrawn with the reason, never half drawn.

## Spelling and text

- **Typographic and non-breaking hyphens are hyphens to the spell check.** A word written with
  U+2011 or U+2010 ("Eis‑Scholle") is checked as the hyphenated word it is; the content-quality
  checks still read the page as it is.
- **An excerpt shows what the visitor sees.** The spell check leaves tag-shaped text out —
  "<FloeId>" in a paragraph is a format's field name, not a word — but the excerpt keeps it: in a
  node's text, a tag can only be text written as entities, which the visitor sees.
- **The dictionary choice is offered in triage too.** Some words only show what they are in their
  sentence (a company name written as plain German, a subject's term), so a triage spelling item
  offers User and Site as well — one decision, two places to make it, one implementation.
- **A word finds its pages in triage.** The search box also matches flagged words, whole and
  ignoring case (a part would bring up every address containing it). Copy for AI says that a
  search is active, never what was searched for: it may be a word from the page.

## Quotes

- **A mixed-systems finding is judged by the page's declared language, and keeps it** ("page
  language: de" in the finding): the screen blames the quotation that does not fit it. Without a
  declared language the app cannot know which system is the page's own, so it blames none and
  says so — the order in which the systems appear decides nothing.
- **The first page language names the wrong end of a same-mark quotation.** “Text“ is wrong in
  every language; which end to name is not. A page declared in several languages has no single
  anchor; the first declared language is the page's main one by how overrides are written, and it
  names the end. Mix and pairing checks still need exactly one language — a guess there would
  raise findings, here it only words one.

## Broken links (logs 06, 07)

- **Two kinds of 404.** *Direct*: an attribute the browser fetches (href, src, a link element,
  a form action, a canonical, a redirect) — the place to fix it is obvious. *Indirect*: an
  address inside a stylesheet, a script, inline data or a data attribute — whether it is ever
  requested depends on a rule, a class or a script. The grading and the reach exist for the
  indirect kind.
- **Give the deepest level that is certain, and stop there.** Naming only the stylesheet already
  narrows the search; a guess presented as an answer is worse than "cannot tell, because …".
- **Evidence, not verdicts.** The app states what it found ("no crawled page requests it");
  whether a reference is still needed or is leftover weight is the operator's decision — it can
  depend on pages beyond the crawl, classes built at run time, or intent.
- **Reach** — who runs into a 404, most serious first:
  1. Every page view — script, image, fetched link element; a stylesheet rule that fires on
     load or on matching devices (a media or container query on screen size, orientation,
     colour scheme). A device condition counts as a page view: nobody has to act.
  2. When opened (a click, a tap or the keyboard) — link, form action, redirect; a rule in a
     state (`:hover`, `:focus`, `:checked`; a state under a media query is still a state).
  3. Only if a script acts — data attribute, inline data; a rule needing a class only a script
     names.
  4. Reach undecided — a stylesheet reference the app cannot judge. Its own tier, because
     folding it into another would mix "cannot tell" with an answer.
  5. Never requested — no crawled page and no script names what the rule needs; a link element
     no browser fetches.
  6. Error elsewhere on site — a canonical, the CMS content list, an alternate version search
     engines read.
  7. Origin unknown — nothing saved points to it.
- **Print rules hide nothing from a screen reader.** Printing is no condition under which anyone
  tabs or listens, so rules that apply only when printing, and print stylesheets, are not read for
  the hidden-focus and empty-link checks; "not print", "all" and "screen" queries still are.
- **Triage by verdict only.** Nothing turns a 404 into a ticket on its own. An entry is where
  the fix is made: the stylesheet, the shared address (same reference on several pages), or the
  page.
- **Cache busters** in a ledger key: only the observed form (16 or more hex digits between dots
  or slashes). Other forms are added when they are seen, not in advance.

## Triage scope

- **In triage:** content quality, broken links, self-links, canonical issues (chains, several
  tags), images, search appearance, the main heading, forwarding links and spelling — what someone
  fixes in a page, a stylesheet or a shared component.
- **A finding is decided once.** A canonical whose target was not found belongs to Broken links;
  the canonical source leaves it out.
- **Images:** metadata is a defect only when it is personal or reveals a location (GPS, a camera
  serial, a person's name) — the editing program and copyright are information, often deliberate.
  Size counts only for an image a page shows (img, srcset, a stylesheet background), not for one
  offered as a link (a high-resolution download); a tiny image can be a spacer or a pixel on
  purpose. So images need "who uses it, and how" before they enter triage; the entry follows the
  broken-link rule (one user: that page or stylesheet; several: the image with its users listed).
  Who uses an image is found in the broken-links pass (log 38), so asset quality runs before it:
  one read of the pages and stylesheets for both. Only images with a finding triage can take are
  looked up, whatever the switches say — log 38 does not change with a setting. Tiny images and
  names of people are off by default: whether they matter depends on the site.
- **A defect repeated exactly on several pages is one entry**, decided and filed once, with its
  pages listed: two or more pages carrying the same type, detail and context (numbers aside) is
  the evidence of one source — template, component or shared fragment. Styled headings are
  grouped by pattern (tag and heading class), whatever the texts, each page listed with its texts:
  the defect is how a component renders its titles; grouping per heading text would make an entry
  of every title that happens to repeat. The app says "the same on N pages", never "component":
  that a class belongs to a component cannot be read from the HTML; the page texts let the
  operator choose the team. Page tickets do not mention shared entries.
- **Search appearance.** SEO starts from what a search engine does with a page, not from the
  fields the app happens to collect: is the page in search, and if so, how does it appear there.
  Only pages that may appear in search get items (no noindex in the page or by header, no
  canonical naming another page; log 08 says which per page). Everything here is edited per page
  by the page's editor, so every item is a page item — a title several pages share is an item on
  each of them, naming the others; one shared entry would be fixed by nobody. Search engines cut
  titles and descriptions by width in pixels, so the width is estimated from letter widths, and
  they may rewrite either text anyway. A search result preview shows the page as a result would
  likely show it. Not triaged: titles that are too short, a title that is only the site name,
  title equal to H1 — matters of taste. `index,nofollow` stays out of triage: a page in search
  whose links lead to processes behind a login is a deliberate choice.
- **Title patterns.** Many sites add their name to every title. That text is sacrificial — a
  result may cut it — so only the page's own part is measured for cut-off, and a page whose title
  does not carry the added text exactly is the anomaly worth an item. The pattern is a setting
  (`Seo.TitleTemplates`), never detected and never in the code.
- **Search appearance and main heading ride along.** Every SEO item is the entry for going deeper,
  not the list an operator starts from: on by default they would put a large share of a site on
  the list at once. They ride along into the ticket of a page open for something else, and an SEO
  pass switches them on. Canonical issues stay on: rare, and two signals contradicting each other.
- **Shared texts ride along and stay on the page.** A title or description several pages share
  is fixed on the page, so the item belongs there; grouping the set into one entry was ruled out
  for that reason. Where the other page also has the same description and main heading, it is
  most likely one page at two addresses (a subdomain is another address; search engines group
  duplicates across hosts), and the advice is to choose one address, not to write new texts.
- **Canonicals and the main heading.** A canonical naming an address that redirects, or a page
  kept out of search while the page itself is not, sends two signals that contradict each other:
  both are canonical issues (log 16), on. The main heading: no H1 and several different H1s are
  items, on. Several H1s with the same text are off: a template may write one per layout, each
  hidden at the other width, so a screen reader hears the heading once — which the HTML alone
  cannot show.
- **Meta keywords** are a finding wherever they are on a page in search: no search engine ranks by
  them, one counts stuffed keywords against a page, they show the page's aims to anyone, and nobody
  sees them go out of date. The advice is to empty the field in the page's settings, after checking
  that the site's own search does not read it.
- **Rounds: a touched page waits.** Tickets go out per page and every editor fixes what is on
  their page in one go; a team works in rounds. A page ticketed before goes behind every page
  never ticketed, the one ticketed longest ago first, so each round reaches pages no editor has
  seen yet. Broken links that only a script may request, that nothing requests or whose reach is
  undecided ride along: they reach a ticket with their page, not a page of their own.
- **What triage counts is a setting, not a session choice:** one set for every site, in
  Settings → Triage; Triage shows it and links there. A site that needs other rules gets its
  own copy of the app with its own configuration — per-site switches would multiply settings.
  Shared entries are listed only when their switch is on; template defects that arrive grouped
  (embedded BOM, styled headings) are on by default.

## Cards and tickets

- **Card wording.** A card names WCAG only where a success criterion applies; it does not keep
  saying where it does not. Each card starts with what the finding is about (Typography, Markup,
  Search engines, Images, Privacy …).
- **Likely WCAG issue: the person first.** "WCAG violation" at the top of a ticket starts a
  debate — does it apply, who is to blame, is the auditor right — and the fix waits. What a person
  using assistive technology runs into, and how small the fix is, starts the fix. So the app names
  the kind "likely WCAG issue", the text opens with the effect on a person, and the criterion and
  the legal benchmark close the card: true and complete for whoever must justify the work, not in
  the way of whoever does it.
- **A cited rule says where it stands.** Each citation in a card ("LB12a", "2.4.4") is a link in
  place, and a guard test holds every card to it: a rule cited, in English or German, must have an
  address and the card must list it, so the printed ticket writes it out. A WCAG number is linked
  only when it is listed — "3.2.1" alone could be a version — and techniques only with their word,
  since "H2" is also a heading level.
- **German handouts.** The ticket documents go to the people who fix the pages, so they can be
  German; the app's screens stay English. English is the key: every text is written once in
  English in the code and found in one German table by that text, so nothing has two names to keep
  in step, and the tests fail on any text without its German or German for a text no longer
  written. The German does not address the reader where it can be avoided. WCAG criteria use the
  authorized German titles (WCAG 2.0, the only authorized German version); quoted W3C names stay
  English. Log content stays as written: it is data, read again by other steps.
- **Shared-entry tickets lead with the finding.** A shared entry is fixed once, in the place its
  pages share, so its ticket is read by whoever owns that place: what it is, where and how to fix
  it, and how far it reaches come first; the pages are an appendix that shows the scale. Where a
  card names no fix, the line is left out rather than invented.
- **A ticket says where each word stands.** An editor cannot open the page from a ticket, so each
  occurrence names its place (an attribute, a script, a meta field) in the document's language,
  script strings are decoded for reading, and the engine's own markers and half tags are put in
  words or cut back. A finding's identity is unchanged, so no verdict is lost.
- **The empty-link marker says what is missing.** An editor who does not read HTML or know WCAG
  still sees, at the spot, that something is missing — "[Missing link text]", "[Fehlender
  Linktext]". A defect, not a fact ("no link text"): it says why to care.
- **A PDF font keeps the tables a PDF draws with.** A full subset keeps every table, including a
  colour emoji font's layers for a single check mark, which makes a short ticket too large to
  mail. A viewer reads outlines and metrics by glyph ID and never the layout, colour or hinting
  caches, so those are dropped after subsetting (not for a font whose licence forbids subsetting).
- **A ticket sweep instead of reading tickets one by one.** The ticket PDF is the one view where
  nobody can open the page to fill a gap, so Debug mode can write every page's ticket at once and
  check them. The verdicts are made on a copy in memory: the ledger is the operator's record and is
  never written by a test. The checks say "look here", not "wrong"; the reading decides.

## Comparison

- **Files, not findings.** The Comparison page answers what really changed between two crawls —
  files a crawl lost, a template change, a script changed behind the same address — so it compares
  the saved files, not the findings.
- **Read-only.** Nothing in either snapshot is written. Header sidecars are left out: they differ
  on every crawl and say nothing about the content.
- **Pages as pruned.** One page added to the navigation changes every page, and a value the
  template changes on every delivery (a salt) does the same: compared as saved, every page differs
  and the real changes drown. So a page is compared as its run pruned it — the boilerplate groups
  applied, reference pages whole: a template change shows on the reference pages, every other page
  stays stable unless something on it really changed. Every other file is compared as saved, byte
  for byte.
- **Re-prune, never through the run.** Both snapshots must have been pruned with the same groups,
  so the Comparison re-prunes A and B, or every snapshot of the site, through the boilerplate prune
  alone — not through the run pipeline, where the ledger is written. **A re-prune never changes
  the triage ledger**; a test holds the ledger and every file outside `pruned/` byte-identical. Each
  prune writes `prune-stamp.json`, a fingerprint of the groups, and the page warns when A and B
  differ, differ from the groups in effect, or have no stamp.
- **Noise attributes are boilerplate parts, not comparison rules.** A part of type attribute
  removes the attribute and keeps its element, on every page of the group including its reference
  pages: a value the template writes anew on every delivery is nothing to keep for checking.
  Attributes the analysis reads (language, structure, addresses, the texts it checks, `aria-*`,
  script handlers) are refused: removing them would change findings. One place for what the
  template adds, and the pruned pages are what the analysis reads too. An xpath part that selects
  attributes is refused: the parser hands back their elements, so it would remove the element.
- **Cache busters are set aside in the comparison only.** A site regenerates parts of addresses
  without changing the content: 13 digits as a file name before an extension, 16 or more hex
  digits between dots or slashes — generic conventions, nothing of one site. A file only in A and
  one only in B whose addresses differ only in a cache buster are paired and compared by their
  bytes, so a re-saved image with new pixels still shows. Not shared with the ledger: changing the
  ledger's form would change its keys and orphan saved verdicts.
- **PDF text patterns come from the site's config.** That a run of digits is a date a PDF prints
  is the site's knowledge, not the code's — so the patterns are a setting, named, counted in the
  summary, off while empty, and run without backtracking (principle 3). The subset tag inside a
  TrueType font program, by contrast, is generic (every subsetting generator picks one at random)
  and is set aside always.
- **Blank lines are set aside on pages compared as pruned.** A removed boilerplate part leaves its
  indentation and line breaks behind; the pruned page is already a cleaned-up view, so lines that
  are empty or hold only spaces and tabs carry no content there. Only there, counted on their own
  ("but for blank lines") so nothing drops out unseen; the pruned folder stays what the rules wrote.
- **Every other difference counts**, blanks and line ends included: a whitespace change can be a
  template change nobody sees. Identical files are counted, not listed. Within a changed line each
  run of differing words and signs is marked on its own, words whole.
- **The same snapshot on both sides is refused**, also through another spelling of its path or a
  link. A snapshot without pruned pages is refused with a hint to run its analysis.
- **Pairs are compared in parallel**, a file read in blocks. No time limit anywhere (principle 3).
