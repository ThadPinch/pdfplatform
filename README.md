# PdfPlatform

**The best PDF engine ever created — and the most open and free.**

PdfPlatform is an open-source C# PDF library for .NET that reads, writes, inspects, and rewrites PDF files at every level of the format: from the raw COS object model and cross-reference tables up to canvases, fonts, spot colors, layers, and a flow-layout engine. It is built for one job above all others: **being the core of print production and prepress systems** — including the most advanced ones, which have yet to be built.

No black boxes. No native binaries. No per-server licensing. Every layer of the engine — lexer, parser, object model, serializer, content-stream interpreter — is a public API you can call directly. The whole engine is roughly 9,000 lines of modern C# you can read in an afternoon and step through in a debugger.

- **License:** MIT — free for commercial use, forever
- **Platform:** .NET 10, pure managed C# (Windows, macOS, Linux, containers, serverless)
- **Dependencies:** one — [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp), used only to decode non-JPEG images for embedding; the PDF engine itself is dependency-free

---

## Why it's the best

Claims are cheap. Here is the feature set behind each one.

### It reads real-world PDFs, including broken ones

`PdfFileParser` handles the full range of file structures in the wild: classic cross-reference tables, cross-reference streams, hybrid-reference files, and compressed object streams. When the xref data is missing or lying — which happens constantly with customer-supplied files — it falls back to a **scan-based recovery parser** that rebuilds the object table from the bytes, and tells you it did so (`doc.UsedRecoveryScan`, `doc.RecoveryReason`) so your workflow can flag repaired files instead of silently guessing.

### It exposes the whole format, not a curated subset

The COS layer (`CosDictionary`, `CosArray`, `CosStream`, `CosName`, `CosString`, `CosNumber`, indirect references and a resolver interface) is fully public. Anything the high-level API doesn't cover yet, you can do yourself with the same primitives the engine uses internally — add any dictionary key, build any object graph, splice any stream. You are never blocked waiting for a library vendor to expose a feature of the spec.

### It writes correct, modern output

`PdfSerializer` produces complete files with configurable output: Flate compression for new streams, and optional **object streams + cross-reference streams** for compact modern files (`PdfSaveOptions`). Flate and LZW codecs are built in.

### It understands page content, not just page objects

`ContentStreamProcessor` replays any content stream like a viewer would: it tracks the full graphics state (CTM, fonts, line widths), text matrices, marked-content scopes, and recurses into form XObjects, raising typed events for every **text run, placed image, and painted path**. Events carry what production code actually needs:

- effective **DPI of every placed image** (`ImageRenderEvent.DpiX/DpiY`)
- text render mode, so you can detect invisible OCR text (`RenderMode == 3`)
- true text-space → page-space transforms
- the marked-content/layer stack every event occurred inside

Show-operator strings are decoded the way a viewer decodes them: via `/ToUnicode` CMaps when present, `/Encoding /Differences` glyph names for simple fonts, Latin-1 otherwise.

### It's fast — measured, not claimed

Benchmarked head-to-head against the leading commercial PDF library for .NET on 39 real print-production PDFs — business cards, inserts, wide-format posters, program booklets, up to 216 MB press-ready scans (706 MB total). Same bytes handed to both engines in a single .NET 10 Release process; disk I/O excluded; forced GC between iterations; JIT warmup; best-of-N wall time. Speedup is the geometric mean of per-file ratios (their time ÷ PdfPlatform time — higher is better for PdfPlatform):

| Operation | Speedup | Per-file range | Corpus total (theirs → PdfPlatform) | Allocation ratio |
|---|---:|---:|---:|---:|
| Open + read page boxes | **11.57×** | 1.8–332× | 332 ms → 26 ms | 33.7× |
| Content interpretation (preflight-style walk) | **3.45×** | 1.4–32× | 70.6 s → 51.0 s | 2.8× |
| Load → save round-trip | **2.54×** | 0.9–5.1× | 10.7 s → 6.4 s | 1.8× |
| Set page boxes + save | **2.53×** | 1.0–5.0× | 10.7 s → 6.4 s | 1.8× |
| Merge (copy all pages to a new document) | **2.39×** | 1.3–4.4× | 12.1 s → 6.7 s | 1.8× |
| Variable-data assembly, 25 records | **1.66×** | 1.0–2.9× | 36 ms → 27 ms | 2.8× |
| Generate 100 pages from scratch | 0.58× | — | 19 ms → 34 ms | 2.5× |

The open advantage grows with file size — 4.6× under 1 MB, 76× at 50 MB and above — because PdfPlatform parses the xref and loads objects on demand, so a 216 MB press file opens in ~0.6 ms. Allocation ratio is the incumbent's managed allocations ÷ PdfPlatform's: consistently 1.8–2.8× less GC pressure on heavy operations, 34× less on opens — which is real money in serverless, where memory is the billing dial.

**Output size, too.** Accumulating 25 separately-loaded copies of a record into one document (the variable-data assembly pattern), `PdfImporter`'s content-hash deduplication stores byte-identical subgraphs — template streams, images, fonts — exactly once, where the incumbent's page copy re-embeds them per record:

| Record source | Their output | PdfPlatform output |
|---:|---:|---:|
| 62 KB | 1.5 MB | **68 KB** |
| 97 KB | 2.1 MB | **247 KB** |
| 183 KB | 4.3 MB | **187 KB** |

At production scale this compounds: 50 records of a 308 KB template measured 15.4 MB via the incumbent vs **340 KB** deduped — 45× smaller, pixel-identical render.

**And correct.** Every timed run computed a fingerprint of what it read — page-box width sums across all pages, raw-TrimBox presence counts, and total interpreter text/image/path event counts. All 39 files produced identical fingerprints from both engines with zero parse failures on either side, and the write paths produced equivalent output sizes (round-trip 1.01, box mutation 1.01, merge 1.00).

**Where the incumbent wins**, in fairness: generating 100 dense pages from scratch (19 ms vs 34 ms — its writer streams objects out as pages complete, PdfPlatform serializes by reachability after building; sub-millisecond difference at typical invoice sizes), one 108 MB scanned file that round-tripped 15% slower (the only file in the corpus where PdfPlatform lost at anything), and format breadth PdfPlatform deliberately omits: encryption, incremental-update writing, tagged PDF / PDF-A, signatures, and AcroForm creation.

---

## Feature matrix

| Area | What you get |
|---|---|
| **Parsing** | Classic xref, xref streams, hybrid files, object streams, on-demand object loading, scan-based recovery of damaged files, encryption detection |
| **Writing** | Full serializer, Flate/LZW codecs, object-stream output, stream compression options, in-place document editing (load → modify → save) |
| **Object model** | Complete public COS layer: dictionaries, arrays, streams, names, strings, numbers, indirect references, resolver interface |
| **Canvas** | Every core operator: paths, fills (nonzero/even-odd), clipping, transforms, graphics state, Gray/RGB/CMYK color, raw operator escape hatch |
| **Color** | DeviceCMYK, **Separation (spot) colors** with tint transforms over CMYK alternates, spot-color enumeration on existing files |
| **Layers (OCG)** | Create optional content groups, draw into `BDC`/`EMC` scopes, and **flatten layers** — permanently removing hidden layers from content streams |
| **Fonts** | All 14 standard fonts with real AFM metrics; TrueType parsing and embedding; text measurement; WinAnsi encoding |
| **Text drawing** | Aligned text (left/center/right, vertical centering, rotation), word wrapping, line breaking with accurate width measurement |
| **Images** | JPEG pass-through as DCTDecode (including Adobe CMYK inversion handling); PNG and other formats decoded to RGB with alpha preserved as an SMask |
| **Pages** | MediaBox / CropBox / **TrimBox / BleedBox / ArtBox** read and write, rotation, content-stream splicing before or after existing content |
| **Import / merge** | `PdfImporter` deep-copies pages between documents — as pages, or as **form XObjects for imposition** — with optional content deduplication |
| **Content analysis** | Typed render events for text, images, and paths; per-image effective DPI; invisible-text detection; layer/marked-content attribution |
| **Inspection** | Color-space description, spot-colorant collection, font-embedding checks (`PdfInspection`) |
| **Annotations** | Text annotations (contents, color, author, subject, icon), URI link annotations, generic annotation access on any page |
| **Layout** | Flow-layout engine: styled paragraphs and runs, percent-column tables with colspan, nesting, borders, backgrounds and repeated headers, images, rules, hyperlinks, automatic page breaks |
| **Barcodes** | QR codes (all error-correction levels) and 1D barcodes — Code 128 (auto subset A/B/C), Code 39, EAN-13, UPC-A, Interleaved 2 of 5 — all emitted as resolution-independent vector form XObjects |
| **Geometry** | Matrix algebra, rectangles with box semantics, point/inch unit conversion |

---

## Install

```bash
git clone https://github.com/ThadPinch/pdfplatform.git
cd pdfplatform
dotnet build
```

Reference `PdfPlatform.csproj` from your solution, or `dotnet pack` it into a local NuGet package.

---

## Quick start

### Create a PDF and draw on it

```csharp
using FrontEndSuite.PdfPlatform.Canvas;
using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Document;
using FrontEndSuite.PdfPlatform.Fonts;
using FrontEndSuite.PdfPlatform.Geometry;

using var doc = PdfDocument.Create();
var page = doc.AddPage(PdfRect.Letter);

var resources = new CosDictionary();
page.Dictionary.Put(CosNames.Resources, resources);
var canvas = new PdfCanvas(page.AddContentStreamAfter(), resources, doc);

canvas.SetFillCmyk(0, 0.2, 1, 0)
      .Rectangle(72, 640, 468, 80)
      .Fill()
      .SetFillGray(0)
      .ShowTextAligned(StandardFont.HelveticaBold, 24, "Hello, PdfPlatform",
                       306, 672, TextHorizontalAlignment.Center);

File.WriteAllBytes("hello.pdf", doc.Save());
```

### Load any file — even a damaged one — and inspect it

```csharp
using var doc = PdfDocument.LoadFile("customer-upload.pdf");

if (doc.UsedRecoveryScan)
    Console.WriteLine($"File was damaged and rebuilt: {doc.RecoveryReason}");

foreach (var page in doc.Pages)
{
    var trim = page.TrimBox;
    Console.WriteLine(
        $"Page {page.PageNumber}: trim {trim.Width / 72.0:0.###}\" x {trim.Height / 72.0:0.###}\", rotation {page.Rotation}");
}
```

### Spot colors and print layers

```csharp
var dieline = PdfSeparationColor.Create(doc, "Dieline", c: 0, m: 1, y: 0, k: 0);
var dielineLayer = PdfOptionalContentGroup.Create(doc, "Dieline");

canvas.BeginLayer(dielineLayer)
      .SetStrokeSeparation(dieline)
      .SetLineWidth(0.5)
      .Rectangle(page.TrimBox)
      .Stroke()
      .EndLayer();
```

### Preflight: image DPI, invisible text, spot-ink usage

```csharp
using FrontEndSuite.PdfPlatform.Parsing;

sealed class Preflight : IContentListener
{
    public void OnImage(ImageRenderEvent e)
    {
        if (e.DpiX < 300 || e.DpiY < 300)
            Console.WriteLine($"Low-res image: {e.DpiX:0} x {e.DpiY:0} dpi");
    }

    public void OnText(TextRenderEvent e)
    {
        if (e.RenderMode == 3)
            Console.WriteLine("Invisible text found (OCR layer?)");
    }
}

new ContentStreamProcessor(new Preflight()).ProcessPage(doc.GetPage(1));

var spots = new HashSet<string>();
PdfInspection.CollectSpotColorNames(
    doc.GetPage(1).Resources?.GetAsDictionary(CosNames.ColorSpace), spots);
```

### Imposition: N-up from any source document

```csharp
using var source = PdfDocument.LoadFile("business-card.pdf");
using var sheet = PdfDocument.Create();

var importer = new PdfImporter(sheet, dedupIdenticalContent: true);
var card = importer.ImportPageAsForm(source.GetPage(1));

var press = sheet.AddPage(new PdfRect(0, 0, 936, 612)); // 13x8.5" sheet
var res = new CosDictionary();
press.Dictionary.Put(CosNames.Resources, res);
var c = new PdfCanvas(press.AddContentStreamAfter(), res, sheet);

for (var row = 0; row < 3; row++)
    for (var col = 0; col < 4; col++)
        c.AddFormXObject(card, PdfMatrix.Translate(36 + col * 216, 36 + row * 180));

File.WriteAllBytes("imposed.pdf", sheet.Save(new PdfSaveOptions { UseObjectStreams = true }));
```

### Flow layout: invoices, tickets, reports

```csharp
using FrontEndSuite.PdfPlatform.Layout;

var flow = new FlowDocument(doc, PdfRect.Letter,
                            marginTop: 54, marginRight: 54, marginBottom: 54, marginLeft: 54);

flow.Add(new FlowParagraph("INVOICE #10422")
{
    DefaultFont = StandardFont.HelveticaBold,
    DefaultFontSize = 18
});

var para = new FlowParagraph { DefaultFont = StandardFont.Helvetica, DefaultFontSize = 10 };
para.Add(new FlowText("Questions? "));
para.Add(new FlowText("Contact support") { Uri = "https://example.com/support", Underline = true });
flow.Add(para);
```

Tables support percent-based columns, colspan, nesting, cell borders, padding, backgrounds, and headers that repeat across page breaks.

### Barcodes: vector QR and 1D codes

```csharp
var barcode = Barcode1D.Encode("PDFPLATFORM-2026", BarcodeSymbology.Code128);
var form = barcode.CreateFormXObject(doc, heightModules: 60);

// One form-space unit = one module; scale to the final printed size on placement.
var scale = 216f / barcode.WidthWithQuietZones; // 3" wide
canvas.AddFormXObject(form, scale, 0, 0, 1, 72, 400);

var qr = QrCode.Encode("https://example.com/track/10422", QrErrorCorrection.M);
canvas.AddFormXObject(qr.CreateFormXObject(doc), 2, 0, 0, 2, 400, 380);
```

Code 128 selects subsets A/B/C automatically (with digit compaction), EAN-13/UPC-A check digits are computed or validated for you, and every symbology draws as pure vectors with the correct quiet zones built into the bounding box — no rasterization at any size.

---

## Built for prepress

PdfPlatform's feature set maps one-to-one onto the subsystems of a print production platform:

- **File intake & repair** — recovery parsing turns damaged customer uploads into workable documents, with an audit trail of what was repaired.
- **Preflight** — per-image effective DPI, font-embedding checks, spot-colorant enumeration, color-space description, invisible-text detection, page-box validation (trim/bleed/art), all via public APIs with no rendering step.
- **Imposition** — import any page from any document as a form XObject and place it with full matrix control: step-and-repeat, N-up, cut-and-stack, work-and-turn. Content deduplication keeps ganged sheets small.
- **Variable data printing (VDP)** — shared static content as form XObjects plus per-record canvas drawing, aligned text, wrapped text, and vector QR codes and 1D barcodes (Code 128, Code 39, EAN/UPC, ITF) for tracking, mail, and retail.
- **Dielines, varnish, and white ink** — Separation color spaces with proper tint transforms, drawn into optional content groups your RIP and your customers can toggle.
- **Layer processing** — flatten proofing or versioning layers permanently before output with `ContentLayerFlattener`.
- **Document assembly** — merge, reorder, stamp, and splice content before or after existing page content without disturbing it.

These are the cores of prepress systems — including ones more advanced than anything currently shipping. The primitives are all here, they are all public, and they are all free.

---

## Architecture

| Namespace | Contents |
|---|---|
| `Cos` | The raw PDF object model: dictionaries, arrays, streams, names, strings, numbers, indirect references |
| `IO` | Lexer, object parser, file parser (with recovery), serializer, Flate/LZW codecs |
| `Document` | `PdfDocument`, `PdfPage`, page boxes, importer, image/form XObjects, spot colors, layers, annotations, inspection |
| `Canvas` | Content-stream drawing API, text alignment/wrapping extensions, QR codes |
| `Fonts` | Standard-14 fonts with AFM metrics, TrueType parsing/embedding, text measurement |
| `Parsing` | Content-stream interpreter, typed render events, ToUnicode CMaps, layer flattener |
| `Layout` | Flow-layout engine: paragraphs, tables, images, rules, links |
| `Geometry` | Matrices, rectangles, unit conversion |

Design rules the codebase follows:

1. **Everything is public.** The escape hatch is the same API the engine uses.
2. **Pure managed code.** No P/Invoke, no native renderer, nothing to install on a server.
3. **Fidelity to the spec and to real files.** Where viewers are lenient, the parser is lenient — and says so.

---

## FAQ

**Is PdfPlatform free for commercial use?**
Yes. MIT license. No royalties, no server counting, no "community edition" ceiling.

**Does it run on Linux / in Docker / in AWS Lambda?**
Yes. It is pure C# targeting .NET 10 with no native dependencies.

**Can it repair corrupt PDF files?**
Yes — when cross-reference data is missing or wrong, it rebuilds the object table by scanning the file, and reports that recovery was used.

**Does it support CMYK and spot colors?**
Yes. DeviceCMYK drawing operators and Separation color spaces with tint transforms are first-class APIs, and spot colorants in existing files can be enumerated.

**Can it generate PDF/X or tagged PDF?**
Not as a one-call API today — but because the full COS layer is public, output-intent dictionaries, metadata streams, and structure trees can be built with the same primitives the engine itself uses.

---

## License

MIT. See [LICENSE](LICENSE).
