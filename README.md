# Blue Protocol — Recipe JSON Builder & Focus Calculator

Two companion tools for planning and optimizing crafting in **Blue Protocol**:

1. **Recipe JSON Builder** — a lightweight web app for creating and validating recipe data.
2. **Focus Calculator** — a .NET console utility that estimates focus usage, yields, and throughput from your recipe data.

> This repo groups both tools so you can design data in the Builder and analyze it in the Calculator with the same schema.

---

## 🚀 Quick Start

### Prerequisites

* **Recipe JSON Builder**: any modern browser (Chrome, Edge, Firefox, Safari). No server required.
* **Focus Calculator**: [.NET SDK 7 or 8](https://dotnet.microsoft.com/download) installed and on your PATH.

### Clone

```bash
# SSH
git clone git@github.com:<you>/<repo>.git
# or HTTPS
git clone https://github.com/<you>/<repo>.git
cd <repo>
```

---

## 🧱 Repository Structure

```text
<repo>/
├─ builder/                 # Recipe JSON Builder (static web app)
│  ├─ index.html            # Main UI
│  ├─ assets/               # CSS/JS/fonts/images
│  └─ docs/                 # Optional screenshots for README
│
├─ focus-calculator/        # .NET console app
│  ├─ BlueProtocolFocusCalculator.csproj
│  ├─ Program.cs            # Entry point
│  └─ Models/               # Recipe.cs, FocusLine.cs, etc.
│
├─ examples/                # Sample data for quick testing
│  ├─ recipes.example.json
│  └─ focus-lines.example.json
│
└─ README.md                # You are here
```

> Folder names may differ in your local project; update as needed. The tools work independently.

---

## 🍳 Recipe JSON Builder (Web)

A small, single‑page app for drafting, validating, and exporting recipe data as JSON for use with the Focus Calculator.

### Run It

* **Simplest:** open `builder/index.html` directly in your browser (double‑click).
* **Local server (optional):**

  * Python: `python -m http.server -d builder 8080`
  * Node: `npx http-server builder -p 8080`
  * Then visit `http://localhost:8080`.

### Core Features

* Add/edit recipes with live validation.
* Support for **fixed yield** and **variable yield (min/max)**.
* Ingredient editor with per‑craft quantities.
* Toggle for **mineable**/gatherable actions.
* Export/import **JSON** that the Focus Calculator reads directly.

### Data Model / JSON Schema

Each recipe adheres to the following schema (loose JSON schema shown for readability):

```jsonc
{
  "Name": "Iron Ingot",
  "TimePerCraftSeconds": 12.5,      // ≥ 0
  "FocusCost": 4,                   // ≥ 0 (per craft or action)
  "Yield": 1,                       // optional when using YieldMin/Max
  "YieldMin": 1,                    // optional; use both min & max for variable yields
  "YieldMax": 3,
  "IsMineable": false,              // true for gather/mine actions
  "Ingredients": {                  // name → units per craft (all ≥ 0)
    "Iron Ore": 3,
    "Coal": 1
  }
}
```

**Validation rules** implemented by/assumed in the Builder & Calculator:

* `Name` is required and unique within your dataset.
* `TimePerCraftSeconds ≥ 0`.
* `FocusCost ≥ 0`.
* You must supply **either** `Yield` **or** (`YieldMin` **and** `YieldMax`).
* When `YieldMin/Max` are provided, `YieldMin ≥ 0`, `YieldMax ≥ YieldMin`.
* All `Ingredients` quantities are `≥ 0`.

### Export/Import

* **Export:** saves an array of recipe objects to a `.json` file.
* **Import:** loads a `.json` file produced by this tool or that follows the schema above.

> Tip: keep your canonical recipes in `data/recipes.json` at the repo root, and track changes via Git.

---

## 🧮 Focus Calculator (.NET)

A command‑line tool that consumes your recipe JSON and reports throughput, expected yield, and focus usage per unit time.

### Build & Run

```bash
cd focus-calculator
# Restore & build
dotnet build -c Release
# Run (basic)
dotnet run --configuration Release
```

> If your app expects input file paths or flags, pass them after `--`:
> `dotnet run -- -i ../data/recipes.json -o ../out/report.csv`

### Models

The calculator uses these core models (simplified):

```csharp
class Recipe {
  public string Name { get; set; } = "";
  public double TimePerCraftSeconds { get; set; } = 0;
  public double FocusCost { get; set; } = 0;
  public double Yield { get; set; } = 1;          // fixed yield
  public double? YieldMin { get; set; }           // variable yield (min)
  public double? YieldMax { get; set; }           // variable yield (max)
  public bool IsMineable { get; set; } = false;
  public Dictionary<string, double>? Ingredients { get; set; }
}

class FocusLine {
  public int Level { get; set; }
  public string Action { get; set; } = "";        // e.g. Craft, Mine
  // …additional fields as needed
}
```

### Calculations (Reference)

Given a recipe and an **expected yield**:

```text
crafts_per_hour  = 3600 / TimePerCraftSeconds
expected_yield   = Yield            // if fixed yield provided
                 = (YieldMin + YieldMax) / 2  // if variable
items_per_hour   = crafts_per_hour * expected_yield
focus_per_hour   = crafts_per_hour * FocusCost
focus_per_item   = FocusCost / expected_yield
```

> If your implementation deviates, update this section to match your logic so results remain transparent.

### Example

Input `examples/recipes.example.json`:

```json
[
  {
    "Name": "Iron Ingot",
    "TimePerCraftSeconds": 12.5,
    "FocusCost": 4,
    "Yield": 1,
    "IsMineable": false,
    "Ingredients": { "Iron Ore": 3, "Coal": 1 }
  },
  {
    "Name": "Copper Vein",
    "TimePerCraftSeconds": 2.0,
    "FocusCost": 1,
    "YieldMin": 1,
    "YieldMax": 3,
    "IsMineable": true,
    "Ingredients": {}
  }
]
```

Expected output (human‑readable summary or CSV):

```text
Recipe        Crafts/h  Items/h  Focus/h  Focus/Item
Iron Ingot      288.0    288.0   1152.0       4.0000
Copper Vein    1800.0   2700.0   1800.0       0.6667
```

> Customize output format (console table/CSV/JSON) to suit your workflow.

---

## 🔁 Typical Workflow

1. **Draft data** in the **Recipe JSON Builder** and export `recipes.json`.
2. **Analyze** with the **Focus Calculator** to get throughput & focus metrics.
3. **Iterate** on timings, yields, and ingredients until you hit desired targets.

---

## 🧪 Development Notes

* **Builder UI** uses a minimal, modern theme (see `builder/index.html`).
* **Serialization** uses `System.Text.Json` for speed and small payloads.
* Prefer *explicit units* in property names (e.g., `TimePerCraftSeconds`).
* Keep calculations pure & unit‑tested where possible.

---

## 📸 Screenshots

Place images under `builder/docs/` and reference them here:

```markdown
![Recipe Builder UI](builder/docs/builder-ui.png)
![Focus Calculator Output](builder/docs/calculator-output.png)
```

---

## 🗺️ Roadmap

* [ ] Batch editing & find/replace in Builder
* [ ] Ingredient auto‑complete & unit presets
* [ ] CSV export from Calculator
* [ ] Sensitivity analysis (±% time/yield)
* [ ] Multi-step recipe chains (cost propagation)

---

## 🤝 Contributing

PRs welcome! Please:

1. Open an issue describing the change.
2. Keep code style consistent with existing files.
3. Add tests or example data when relevant.

---

## 📄 License

Choose a license for your repo (e.g., **MIT**). If you pick MIT, drop a `LICENSE` file with:

```text
MIT License (c) <year> <your name>
```

---

## 🙏 Acknowledgements

* Thanks to the Blue Protocol community for data collection and theorycrafting.
* When sharing in public, avoid distributing proprietary game assets.

---

### Appendix: Minimal JSON Schema (informal)

```jsonc
// Array<Recipe>
[
  {
    "Name": "string",                      // required, unique
    "TimePerCraftSeconds": 0.0,             // number ≥ 0
    "FocusCost": 0.0,                       // number ≥ 0
    "Yield": 1.0,                           // number ≥ 0 (omit if using min/max)
    "YieldMin": 0.0,                        // number ≥ 0
    "YieldMax": 0.0,                        // number ≥ YieldMin
    "IsMineable": false,                    // boolean
    "Ingredients": { "name": 0.0 }         // map<string, number≥0>
  }
]
```
