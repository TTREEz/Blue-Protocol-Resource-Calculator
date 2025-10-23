using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;


namespace BlueProtocolFocusCalculator
{
    // ---------- Data Models ----------
    class Recipe
    {
        public string Name { get; set; } = "";
        public double TimePerCraftSeconds { get; set; } = 0; // time per craft or per mine/gather action (seconds)
        public double FocusCost { get; set; } = 0;   // per craft
        public double Yield { get; set; } = 1;       // items per craft (fixed if no min/max)
        public double? YieldMin { get; set; }        // optional variable yield (min)
        public double? YieldMax { get; set; }        // optional variable yield (max)
        public bool IsMineable { get; set; } = false;
        public Dictionary<string, double>? Ingredients { get; set; } // name -> units per craft
    }


    class FocusLine
    {
        public int Level { get; set; }
        public string Action { get; set; } = "";   // Craft/Mine/Gather
        public string Material { get; set; } = "";
        public double Crafts { get; set; }         // craft actions at this node
        public double Yield { get; set; }          // effective output per craft at this node
        public double UnitsRequested { get; set; } // total units requested downstream for this node
        public double FocusUsed { get; set; }
        public double TimeUsedSeconds { get; set; } // crafts * recipe.TimePerCraftSeconds
    }


    // Trim/AOT-safe JSON metadata for the shapes we read/write
    [JsonSerializable(typeof(List<Recipe>))]
    [JsonSerializable(typeof(Dictionary<string, Recipe>))]
    internal partial class RecipesJsonContext : JsonSerializerContext { }


    static class Program
    {
        static Dictionary<string, Recipe> _recipes = new(StringComparer.OrdinalIgnoreCase);
        static string _recipesPath = "";
        static bool _recipesIsArrayFormat = true; // preserve array vs map when saving

        static void Main(string[] args)
        {
            Console.WriteLine("== Blue Protocol Focus Calculator ==\n");

            _recipesPath = GetRecipesPathFromArgsOrDefault(args);

            try
            {
                LoadOrCreateRecipesJson(_recipesPath);
                Console.WriteLine($"Using recipes file: {_recipesPath}\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to load recipes.json: " + ex.Message);
                Pause();
                return;
            }

            var cycle = FindCycle();
            if (cycle is not null)
            {
                Console.WriteLine("ERROR: A recipe cycle was detected: " + string.Join(" -> ", cycle));
                Console.WriteLine("Fix recipes.json to remove cycles and re-run.");
                Pause();
                return;
            }

            // ---- Main Menu Loop ----
            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("Main Menu");
                Console.WriteLine("  1) Calculate focus plan");
                Console.WriteLine("  2) List materials");
                Console.WriteLine("  3) Reload recipes.json");
                Console.WriteLine("  4) Recipe Builder (add/edit/delete)");
                Console.WriteLine("  Q) Quit");
                var choice = PromptChoice("\nSelect an option: ", new[] { "1", "2", "3", "4", "Q" }, defaultValue: "1");

                try
                {
                    if (choice.Equals("1", StringComparison.OrdinalIgnoreCase))
                    {
                        RunCalculatorFlow();
                        Pause();
                    }
                    else if (choice.Equals("2", StringComparison.OrdinalIgnoreCase))
                    {
                        PrintMaterials();
                        Pause();
                    }
                    else if (choice.Equals("3", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            LoadOrCreateRecipesJson(_recipesPath);
                            Console.WriteLine("Reloaded recipes.json.");
                            cycle = FindCycle();
                            if (cycle is not null)
                                Console.WriteLine("WARNING: Cycle detected after reload: " + string.Join(" -> ", cycle));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Failed to reload recipes: " + ex.Message);
                        }
                        Pause();
                    }
                    else if (choice.Equals("4", StringComparison.OrdinalIgnoreCase))
                    {
                        RunRecipeBuilderFlow();
                        Pause();
                    }
                    else if (choice.Equals("Q", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("\nUnexpected error: " + ex.Message);
                    Pause();
                }

                Console.Clear();
                Console.WriteLine("== Blue Protocol Focus Calculator ==\n");
            }
        }

        static string FormatDuration(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var ts = TimeSpan.FromSeconds(Math.Round(seconds));
            // HH:mm:ss if >= 1 hour, else mm:ss
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            return $"{ts.Minutes:00}:{ts.Seconds:00}";
        }

        static double SumTimeSeconds(List<FocusLine> lines) => lines.Sum(ln => ln.TimeUsedSeconds);

        enum YieldMode { Safe, Optimistic, Average }

        static double EffectiveYield(Recipe r, YieldMode mode)
        {
            // If min/max present, pick based on mode; else fall back to fixed Yield
            var hasMin = r.YieldMin.HasValue && r.YieldMin.Value > 0;
            var hasMax = r.YieldMax.HasValue && r.YieldMax.Value > 0;

            if (hasMin || hasMax)
            {
                double min = hasMin ? r.YieldMin!.Value : r.Yield;
                double max = hasMax ? r.YieldMax!.Value : r.Yield;

                return mode switch
                {
                    YieldMode.Safe       => Math.Max(1, min),
                    YieldMode.Optimistic => Math.Max(1, max),
                    _                    => Math.Max(1, (min + max) / 2.0),
                };
            }

            return Math.Max(1, r.Yield);
        }

        static void PrintLeafChecklist(List<FocusLine> lines)
        {
            // Aggregate only leaf nodes (no ingredients)
            var leafAgg = new Dictionary<string, (double units, double yieldEff, Recipe rec)>(StringComparer.OrdinalIgnoreCase);

            foreach (var ln in lines)
            {
                if (!_recipes.TryGetValue(ln.Material, out var r)) continue;
                bool isLeaf = r.Ingredients == null || r.Ingredients.Count == 0;
                if (!isLeaf) continue;

                if (!leafAgg.TryGetValue(ln.Material, out var acc))
                    acc = (0, ln.Yield > 0 ? ln.Yield : Math.Max(1, r.Yield), r);

                acc.units += ln.UnitsRequested;
                if (ln.Yield > 0) acc.yieldEff = ln.Yield;

                leafAgg[ln.Material] = acc;
            }

            var rows = new List<(string action, string material, double crafts, double yield, double units, double focus, double timeSec)>();
            foreach (var kv in leafAgg)
            {
                var name = kv.Key;
                var (unitsTotal, yieldEff, r) = kv.Value;

                double crafts = Math.Ceiling(unitsTotal / Math.Max(1, yieldEff));
                double focus  = crafts * r.FocusCost;
                double time   = crafts * Math.Max(0, r.TimePerCraftSeconds);
                string action = r.IsMineable ? (r.FocusCost > 0 ? "Mine" : "Gather")
                                            : (r.FocusCost > 0 ? "Craft" : "Craft");

                rows.Add((action, name, crafts, yieldEff, unitsTotal, focus, time));
            }

            foreach (var row in rows
                .OrderByDescending(x => x.focus)
                .ThenBy(x => x.action, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.material, StringComparer.OrdinalIgnoreCase))
            {
                var timeTail = row.timeSec > 0 ? $" + {FormatDuration(row.timeSec)}" : "";
                if (row.focus > 0)
                    Console.WriteLine($"- {row.action} {row.crafts}x {row.material} → {row.focus:F2} Focus (Yield {row.yield}, Needs {row.units}){timeTail}");
                else
                    Console.WriteLine($"- {row.action} {row.units} {row.material} (no Focus cost){timeTail}");
            }
        }

        // ---------- Print Tree ----------
        static void PrintTree(List<FocusLine> lines, bool bottomUp = false)
        {
            IEnumerable<FocusLine> seq = lines;
            if (bottomUp) seq = lines.AsEnumerable().Reverse();

            foreach (var ln in seq)
            {
                string indent = new string(' ', ln.Level * 2);
                if (ln.Action == "Gather" && ln.FocusUsed == 0)
                    Console.WriteLine($"{indent}- {ln.Action} {ln.UnitsRequested} {ln.Material} (no Focus cost)");
                else
                    Console.WriteLine($"{indent}- {ln.Action} {ln.Crafts}x {ln.Material} → {ln.FocusUsed:F2} Focus (Yield {ln.Yield}, Req {ln.UnitsRequested})");
            }
        }

        // ---------- Get Leafs ----------
        static void PrintLeafPaths(List<FocusLine> lines)
        {
            // lines are in pre-order (root then children). We'll walk them and
            // maintain a stack of the current path. When we hit a leaf, we print
            // that path from leaf → root with leaf at indent 0.
            var stack = new List<FocusLine>();

            foreach (var ln in lines)
            {
                // Align stack to this node's level in the original tree
                while (stack.Count > ln.Level)
                    stack.RemoveAt(stack.Count - 1);

                // push current node
                stack.Add(ln);

                // is this node a leaf (no ingredients)?
                if (!_recipes.TryGetValue(ln.Material, out var r)) continue;
                bool isLeaf = r.Ingredients == null || r.Ingredients.Count == 0;
                if (!isLeaf) continue;

                // Print the current path from leaf → root, with proper per-path indentation
                int indent = 0;
                for (int i = stack.Count - 1; i >= 0; i--, indent++)
                {
                    var node = stack[i];
                    string pad = new string(' ', indent * 2);

                    if (node.Action == "Gather" && node.FocusUsed == 0)
                    {
                        var timeTail = node.TimeUsedSeconds > 0 ? $", + {FormatDuration(node.TimeUsedSeconds)}" : "";
                        Console.WriteLine($"{pad}- {node.Action} {node.UnitsRequested} {node.Material} (no Focus cost{timeTail})");
                    }
                    else
                    {
                        var timeTail = node.TimeUsedSeconds > 0 ? $" + {FormatDuration(node.TimeUsedSeconds)}" : "";
                        Console.WriteLine($"{pad}- {node.Action} {node.Crafts}x {node.Material} → {node.FocusUsed:F2} Focus (Yield {node.Yield}, Req {node.UnitsRequested}){timeTail}");
                    }
                }

                Console.WriteLine(); // blank line between leaf-paths
            }
        }



        // ---------- Args / paths ----------
        static string GetRecipesPathFromArgsOrDefault(string[] args)
        {
            string defaultPath = Path.Combine(AppContext.BaseDirectory, "recipes.json");
            if (args is null || args.Length == 0) return defaultPath;

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--recipes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-r", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
                }
                if (a.StartsWith("--recipes=", StringComparison.OrdinalIgnoreCase))
                {
                    var p = a.Substring("--recipes=".Length);
                    if (!string.IsNullOrWhiteSpace(p)) return Path.GetFullPath(p);
                }
            }
            return defaultPath;
        }

        // ---------- Calculator Flow ----------
        static void RunCalculatorFlow()
        {
            var target = PromptForTargetLoop();
            if (target is null) return;

            // Check with both modes
            double oneUnitFocusSafe = CalculateFocus(target, 1, YieldMode.Safe).totalFocus;
            double oneUnitFocusOpt  = CalculateFocus(target, 1, YieldMode.Optimistic).totalFocus;
            bool needsFocus = (oneUnitFocusSafe > 0) || (oneUnitFocusOpt > 0);

            Console.WriteLine();
            if (!needsFocus)
            {
                Console.WriteLine("Note: This material and its prerequisites have no Focus cost. Focus will not limit you.");
                bool exportCsvNF = PromptYesNo("\nExport CSV? (y/n): ");
                double desiredQtyNF = PromptDouble($"\nEnter desired number of {target} (default 1): ", defaultValue: 1, mustBeNonNegative: true);

                var (totalFocusNF, breakdownNF) = CalculateFocus(target, desiredQtyNF, YieldMode.Safe);
                Console.WriteLine("\n=== Leaf-First Paths (Safe) ===");
                PrintLeafPaths(breakdownNF);

                Console.WriteLine("\n=== Base Resources Checklist ===");
                PrintLeafChecklist(breakdownNF);

                Console.WriteLine("\n=== Summary ===");
                Console.WriteLine($"Target: {target}");
                Console.WriteLine($"Desired quantity: {desiredQtyNF}");
                Console.WriteLine($"Total Focus required: {totalFocusNF:F2} (no Focus limit)");
                var totalSecondsNF = SumTimeSeconds(breakdownNF);
                Console.WriteLine($"\nEstimated total time: {FormatDuration(totalSecondsNF)}");


                if (exportCsvNF)
                {
                    var csvPath = ExportCsv(target, desiredQtyNF, breakdownNF, totalFocusNF);
                    Console.WriteLine($"\nCSV exported to: {csvPath}");
                }
                return;
            }

            Console.WriteLine("Choose mode:");
            Console.WriteLine("  1) Desired quantity (shows worst/best-case Focus)");
            Console.WriteLine("  2) Use ALL my Focus (computes exact max, worst/best-case)");
            var mode = PromptChoice("Select 1 or 2: ", new[] { "1", "2" }, defaultValue: "1");

            bool exportCsv = PromptYesNo("\nExport CSV? (y/n): ");

            if (mode == "2")
            {
                double availableFocus = PromptDouble("\nEnter your available Focus: ", mustBeNonNegative: true);

                long bestQtySafe = MaxCraftable(target, availableFocus, YieldMode.Safe);
                long bestQtyOpt  = MaxCraftable(target, availableFocus, YieldMode.Optimistic);

                // Show the conservative (Safe) breakdown
                var (totalFocusSafe, breakdownSafe) = CalculateFocus(target, bestQtySafe, YieldMode.Safe);

                Console.WriteLine("\n=== Leaf-First Paths (Safe) ===");
                PrintLeafPaths(breakdownSafe);
                Console.WriteLine("\n=== Base Resources Checklist (Safe) ===");
                PrintLeafChecklist(breakdownSafe);

                Console.WriteLine("\n=== Optimized Batch ===");
                if (bestQtySafe == bestQtyOpt)
                {
                    Console.WriteLine($"Max craftable: {bestQtySafe} {target}(s) (same for safe/optimistic)");
                }
                else
                {
                    Console.WriteLine($"Max craftable (Safe):       {bestQtySafe}");
                    Console.WriteLine($"Max craftable (Optimistic): {bestQtyOpt}");
                }
                Console.WriteLine($"Focus used (Safe run): {totalFocusSafe:F2} of {availableFocus:F2}");
                var timeSafe = SumTimeSeconds(breakdownSafe);
                // also compute time for optimistic quantity
                var (_, breakdownOpt2) = CalculateFocus(target, bestQtyOpt, YieldMode.Optimistic);
                var timeOpt = SumTimeSeconds(breakdownOpt2);

                Console.WriteLine("\n=== Optimized Batch ===");
                if (bestQtySafe == bestQtyOpt)
                {
                    Console.WriteLine($"Max craftable: {bestQtySafe} {target}(s) (same for safe/optimistic)");
                    Console.WriteLine($"Estimated time: {FormatDuration(timeSafe)}");
                }
                else
                {
                    Console.WriteLine($"Max craftable (Safe):       {bestQtySafe}");
                    Console.WriteLine($"Max craftable (Optimistic): {bestQtyOpt}");
                    Console.WriteLine($"Estimated time (range): {FormatDuration(timeSafe)} → {FormatDuration(timeOpt)}");
                }

                if (exportCsv)
                {
                    var csvPath = ExportCsv(target, bestQtySafe, breakdownSafe, totalFocusSafe);
                    Console.WriteLine($"\nCSV exported to: {csvPath}");
                }
            }
            else
            {
                double desiredQty = PromptDouble($"\nEnter desired number of {target} (default 1): ", defaultValue: 1, mustBeNonNegative: true);
                double availableFocus = PromptDouble("Enter your available Focus (for affordability check): ", mustBeNonNegative: true);

                var (totalFocusSafe, breakdownSafe) = CalculateFocus(target, desiredQty, YieldMode.Safe);
                var (totalFocusOpt,  breakdownOpt ) = CalculateFocus(target, desiredQty, YieldMode.Optimistic);

                Console.WriteLine("\n=== Leaf-First Paths (Safe) ===");
                PrintLeafPaths(breakdownSafe);

                Console.WriteLine("\n=== Base Resources Checklist (Safe) ===");
                PrintLeafChecklist(breakdownSafe);

                Console.WriteLine("\n=== Summary ===");
                Console.WriteLine($"Target: {target}");
                Console.WriteLine($"Desired quantity: {desiredQty}");

                if (Math.Abs(totalFocusSafe - totalFocusOpt) < 1e-9)
                {
                    Console.WriteLine($"Total Focus required: {totalFocusSafe:F2}");
                }
                else
                {
                    Console.WriteLine($"Total Focus required (range): {totalFocusSafe:F2} (Safe) → {totalFocusOpt:F2} (Optimistic)");
                }

                Console.WriteLine($"Available Focus: {availableFocus:F2}");

                if (totalFocusSafe <= availableFocus)
                {
                    double leftover = availableFocus - totalFocusSafe;
                    Console.WriteLine($"✅ You can craft it (Safe). Leftover Focus (Safe): {leftover:F2}");
                }
                else
                {
                    double shortfall = totalFocusSafe - availableFocus;
                    Console.WriteLine($"❌ Not enough Focus (Safe). Additional Focus needed: {shortfall:F2}");

                    long exactMaxSafe = MaxCraftable(target, availableFocus, YieldMode.Safe);
                    long exactMaxOpt = MaxCraftable(target, availableFocus, YieldMode.Optimistic);
                    if (exactMaxSafe == exactMaxOpt)
                        Console.WriteLine($"Exact max craftable now: {exactMaxSafe}");
                    else
                        Console.WriteLine($"Exact max craftable now: {exactMaxSafe} (Safe) → {exactMaxOpt} (Optimistic)");
                }
                var timeSafe = SumTimeSeconds(breakdownSafe);
                var timeOpt  = SumTimeSeconds(breakdownOpt);

                if (Math.Abs(timeSafe - timeOpt) < 1e-6)
                    Console.WriteLine($"Estimated total time: {FormatDuration(timeSafe)}");
                else
                    Console.WriteLine($"Estimated total time (range): {FormatDuration(timeSafe)} (Safe) → {FormatDuration(timeOpt)} (Optimistic)");

                if (exportCsv)
                {
                    var csvPath = ExportCsv(target, desiredQty, breakdownSafe, totalFocusSafe);
                    Console.WriteLine($"\nCSV exported to: {csvPath}");
                }
            }
        }



        // ---------- Recipe Builder ----------
        static void RunRecipeBuilderFlow()
        {
            while (true)
            {
                Console.WriteLine("\nRecipe Builder");
                Console.WriteLine("  1) Add new recipe");
                Console.WriteLine("  2) Edit existing recipe");
                Console.WriteLine("  3) Delete a recipe");
                Console.WriteLine("  B) Back to main menu");
                var choice = PromptChoice("\nSelect: ", new[] { "1", "2", "3", "B" }, defaultValue: "B");
                if (choice.Equals("B", StringComparison.OrdinalIgnoreCase)) return;

                if (choice == "1")
                {
                    AddRecipeInteractive();
                }
                else if (choice == "2")
                {
                    var name = PromptForTargetLoop();
                    if (name is null) continue;
                    EditRecipeInteractive(name);
                }
                else if (choice == "3")
                {
                    var name = PromptForTargetLoop();
                    if (name is null) continue;
                    if (PromptYesNo($"Delete '{name}'? (y/n): "))
                    {
                        if (_recipes.Remove(name))
                        {
                            Console.WriteLine("Removed. Saving...");
                            TryBackup(_recipesPath);
                            SaveRecipesJson(_recipesPath);
                            Console.WriteLine("Saved.");
                        }
                        else Console.WriteLine("Recipe not found.");
                    }
                }
            }
        }

        static void AddRecipeInteractive()
        {
            Console.WriteLine("\n-- Add Recipe --");
            string name;
            while (true)
            {
                name = PromptString("Name (unique): ");
                if (string.IsNullOrWhiteSpace(name)) { Console.WriteLine("Name required."); continue; }
                if (_recipes.ContainsKey(name))
                {
                    Console.WriteLine("That name already exists. Use Edit instead or pick another name.");
                    continue;
                }
                break;
            }

            double focus = PromptDouble("Focus cost per craft (>=0, default 0): ", defaultValue: 0, mustBeNonNegative: true);
            double yield = PromptDouble("Yield per craft (>=1, default 1): ", defaultValue: 1, mustBeNonNegative: true);
            if (yield < 1) yield = 1;

            bool hasVarYield = PromptYesNo("Does this recipe have variable yield? (y/n): ");
            double? yMin = null, yMax = null;
            if (hasVarYield)
            {
                yMin = PromptDouble("Yield MIN per craft (>=1): ", mustBeNonNegative: true);
                yMax = PromptDouble("Yield MAX per craft (>=1): ", mustBeNonNegative: true);
                if (yMin < 1) yMin = 1;
                if (yMax < 1) yMax = 1;
            }

            double timeSec = PromptDouble("Time per craft/gather (seconds, default 5): ", defaultValue: 5, mustBeNonNegative: true);
            bool isMine = PromptYesNo("Is mineable? (y/n): ");

            var ingredients = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Console.WriteLine("\nAdd ingredients (enter blank name to finish).");
            while (true)
            {
                var ingName = PromptString("Ingredient name: ", allowEmpty: true);
                if (string.IsNullOrWhiteSpace(ingName)) break;

                double qty = PromptDouble($"Units of '{ingName}' required per craft: ", mustBeNonNegative: true);
                ingredients[ingName] = qty;

                if (!_recipes.ContainsKey(ingName))
                {
                    bool createStub = PromptYesNo($"'{ingName}' not found. Create placeholder recipe? (y/n): ");
                    if (createStub)
                    {
                        _recipes[ingName] = new Recipe
                        {
                            Name = ingName,
                            FocusCost = 0,
                            Yield = 1,
                            YieldMin = null,
                            YieldMax = null,
                            TimePerCraftSeconds = 5,
                            IsMineable = true,
                            Ingredients = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                        };
                        Console.WriteLine($"Created placeholder: {ingName}. You can edit it later.");
                    }
                    else
                    {
                        Console.WriteLine("OK, leaving it as an external reference.");
                    }
                }
            }

            // IMPORTANT: create the recipe entry (this was missing)
            _recipes[name] = new Recipe
            {
                Name = name,
                FocusCost = focus,
                Yield = yield,
                YieldMin = yMin,
                YieldMax = yMax,
                TimePerCraftSeconds = timeSec,
                IsMineable = isMine,
                Ingredients = ingredients
            };

            Console.WriteLine("Saving...");
            TryBackup(_recipesPath);
            SaveRecipesJson(_recipesPath);
            Console.WriteLine("Saved.");
        }


        static void EditRecipeInteractive(string name)
        {
            if (!_recipes.TryGetValue(name, out var r))
            {
                Console.WriteLine("Recipe not found.");
                return;
            }

            Console.WriteLine($"\n-- Edit Recipe: {name} -- (leave blank to keep current)");

            var newName = PromptString($"Name [{r.Name}]: ", allowEmpty: true);
            if (!string.IsNullOrWhiteSpace(newName) && !newName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (_recipes.ContainsKey(newName))
                {
                    Console.WriteLine("Another recipe already has that name. Keeping the original name.");
                }
                else
                {
                    _recipes.Remove(name);
                    r.Name = newName;
                    name = newName;
                }
            }

            var focusStr = PromptString($"Focus cost per craft [{r.FocusCost}]: ", allowEmpty: true);
            if (double.TryParse(focusStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var newFocus) && newFocus >= 0)
                r.FocusCost = newFocus;

            var yieldStr = PromptString($"Yield per craft [{r.Yield}]: ", allowEmpty: true);
            if (double.TryParse(yieldStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var newYield) && newYield >= 1)
                r.Yield = newYield;

            var yMinStr = PromptString($"Yield MIN per craft [{r.YieldMin?.ToString() ?? "-"}]: ", allowEmpty: true);
            if (double.TryParse(yMinStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var newYMin) && newYMin >= 1)
                r.YieldMin = newYMin;

            var yMaxStr = PromptString($"Yield MAX per craft [{r.YieldMax?.ToString() ?? "-"}]: ", allowEmpty: true);
            if (double.TryParse(yMaxStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var newYMax) && newYMax >= 1)
                r.YieldMax = newYMax;

            var timeStr = PromptString($"Time per craft/gather (sec) [{r.TimePerCraftSeconds}]: ", allowEmpty: true);
            if (double.TryParse(timeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var newTime) && newTime >= 0)
                r.TimePerCraftSeconds = newTime;

            var mineStr = PromptString($"Is mineable? (y/n) [{(r.IsMineable ? "y" : "n")}]: ", allowEmpty: true);
            if (!string.IsNullOrWhiteSpace(mineStr))
                r.IsMineable = mineStr.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);

            r.Ingredients ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                Console.WriteLine("\nIngredients menu:");
                Console.WriteLine("  1) Add / Update ingredient");
                Console.WriteLine("  2) Remove ingredient");
                Console.WriteLine("  3) Clear all ingredients");
                Console.WriteLine("  D) Done");
                var c = PromptChoice("Select: ", new[] { "1", "2", "3", "D" }, defaultValue: "D");
                if (c.Equals("D", StringComparison.OrdinalIgnoreCase)) break;

                if (c == "1")
                {
                    var ingName = PromptString("Ingredient name (blank to cancel): ", allowEmpty: true);
                    if (string.IsNullOrWhiteSpace(ingName))
                    {
                        Console.WriteLine("Cancelled.");
                        continue;
                    }

                    double qty = PromptDouble($"Units of '{ingName}' per craft: ", mustBeNonNegative: true);
                    r.Ingredients[ingName] = qty;

                    if (!_recipes.ContainsKey(ingName))
                    {
                        bool createStub = PromptYesNo($"'{ingName}' not found. Create placeholder recipe? (y/n): ");
                        if (createStub)
                        {
                            _recipes[ingName] = new Recipe
                            {
                                Name = ingName,
                                FocusCost = 0,
                                Yield = 1,
                                YieldMin = null,
                                YieldMax = null,
                                TimePerCraftSeconds = 5,
                                IsMineable = true,
                                Ingredients = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                            };
                            Console.WriteLine($"Created placeholder: {ingName} (mineable, 0 focus).");
                        }
                    }
                }
                else if (c == "2")
                {
                    if (r.Ingredients.Count == 0) { Console.WriteLine("No ingredients to remove."); continue; }
                    Console.WriteLine("Current ingredients:");
                    foreach (var kv in r.Ingredients) Console.WriteLine($"  - {kv.Key}: {kv.Value}");
                    var removeName = PromptString("Ingredient name to remove (blank to cancel): ", allowEmpty: true);
                    if (string.IsNullOrWhiteSpace(removeName)) { Console.WriteLine("Cancelled."); continue; }
                    if (r.Ingredients.Remove(removeName)) Console.WriteLine("Removed.");
                    else Console.WriteLine("Not found.");
                }
                else if (c == "3")
                {
                    if (PromptYesNo("Clear ALL ingredients? (y/n): "))
                    {
                        r.Ingredients.Clear();
                        Console.WriteLine("Cleared.");
                    }
                }
            }

            // <<< these lines + brace were missing; they close the method and save changes
            _recipes[name] = r;

            Console.WriteLine("Saving...");
            TryBackup(_recipesPath);
            SaveRecipesJson(_recipesPath);
            Console.WriteLine("Saved.");
        }

        static void TryBackup(string path, string? newContent = null, int maxBackups = 5)
        {
            try
            {
                if (!File.Exists(path)) return; // nothing to back up yet

                // If we were given the planned new content, skip backup when nothing changes
                if (newContent != null)
                {
                    var existing = File.ReadAllText(path);
                    if (existing == newContent)
                    {
                        Console.WriteLine("(No changes; skipped backup.)");
                        return;
                    }
                }

                // Store backups in a subfolder for tidiness
                var dir = Path.GetDirectoryName(path)!;
                var backupsDir = Path.Combine(dir, "backups");
                Directory.CreateDirectory(backupsDir);

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var baseName = Path.GetFileName(path);            // e.g., recipes.json
                var backupName = baseName + ".bak_" + stamp + ".json";
                var backupPath = Path.Combine(backupsDir, backupName);

                File.Copy(path, backupPath, overwrite: false);
                Console.WriteLine($"Backup: {backupPath}");

                // Prune old backups: keep newest `maxBackups`
                var pattern = baseName + ".bak_*.json";
                var all = Directory.GetFiles(backupsDir, pattern)
                                .OrderByDescending(f => f)     // name order works because of timestamp format
                                .ToList();
                foreach (var old in all.Skip(maxBackups))
                {
                    try { File.Delete(old); } catch { /* ignore */ }
                }

                if (all.Count > maxBackups)
                    Console.WriteLine($"(Pruned to last {maxBackups} backups.)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"(Backup skipped: {ex.Message})");
            }
        }


        // ---------- Recipes JSON (load/save) ----------
        static void LoadOrCreateRecipesJson(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (!File.Exists(path))
            {
                File.WriteAllText(path, SampleRecipesJson);
                Console.WriteLine($"Created starter recipes at:\n  {path}\n");
            }

            var json = File.ReadAllText(path);

            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                TypeInfoResolver = RecipesJsonContext.Default
            };

            // Try array first
            try
            {
                var asArray = JsonSerializer.Deserialize<List<Recipe>>(json, opts);
                if (asArray is not null && asArray.Count > 0)
                {
                    _recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in asArray)
                    {
                        if (string.IsNullOrWhiteSpace(r.Name)) continue;
                        _recipes[r.Name] = Normalize(r);
                    }

                    _recipesIsArrayFormat = true;  // <-- ADDED: remember we loaded an array
                    return;
                }
            }
            catch
            {
                // fall through to map attempt
            }

            // Try map form
            var asMap = JsonSerializer.Deserialize<Dictionary<string, Recipe>>(json, opts);
            if (asMap is null || asMap.Count == 0)
                throw new Exception("recipes.json is empty or invalid.");

            _recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);

            _recipesIsArrayFormat = false;        // <-- ADDED: remember we loaded a map

            foreach (var kv in asMap)
            {
                var r = kv.Value;
                r.Name = string.IsNullOrWhiteSpace(r.Name) ? kv.Key : r.Name;
                _recipes[r.Name] = Normalize(r);
            }
        }



        static bool _enableBackups = true; // set to false to disable backups entirely

        static void SaveRecipesJson(string path)
        {
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                TypeInfoResolver = RecipesJsonContext.Default // keep if you added source-gen
            };

            string output;
            if (_recipesIsArrayFormat)
            {
                var list = _recipes.Values
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                output = JsonSerializer.Serialize(list, opts);
            }
            else
            {
                var map = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in _recipes.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                    map[r.Name] = r;
                output = JsonSerializer.Serialize(map, opts);
            }

            if (_enableBackups)
                TryBackup(path, newContent: output, maxBackups: 5);

            File.WriteAllText(path, output);
        }



        static Recipe Normalize(Recipe r)
        {
            r.Yield = r.Yield <= 0 ? 1 : r.Yield;

            // sanitize min/max
            if (r.YieldMin.HasValue && r.YieldMin.Value <= 0) r.YieldMin = null;
            if (r.YieldMax.HasValue && r.YieldMax.Value <= 0) r.YieldMax = null;

            // if only one side provided, use it to fill the other so Average works
            if (r.YieldMin.HasValue && !r.YieldMax.HasValue) r.YieldMax = r.YieldMin;
            if (r.YieldMax.HasValue && !r.YieldMin.HasValue) r.YieldMin = r.YieldMax;

            r.Ingredients ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (r.Ingredients is not null && r.Ingredients.GetType() != typeof(Dictionary<string, double>))
            {
                r.Ingredients = new Dictionary<string, double>(r.Ingredients, StringComparer.OrdinalIgnoreCase);
            }
            return r;
        }


        // ---------- Target Selection ----------
        static string? PromptForTargetLoop()
        {
            if (_recipes.Count == 0)
            {
                Console.WriteLine("No materials loaded.");
                return null;
            }

            while (true)
            {
                var materials = _recipes.Keys.OrderBy(k => k).ToList();
                Console.WriteLine("Known materials:");
                for (int i = 0; i < materials.Count; i++)
                {
                    var name = materials[i];
                    var r = _recipes[name];
                    var tag = r.IsMineable ? "[mine]" : (r.Ingredients is not null && r.Ingredients.Count > 0 ? "[craft]" : "[solo]");
                    Console.WriteLine($"  {i + 1}. {name} {tag}");
                }

                Console.Write("\nEnter target by number or name (or 'b' to go back): ");
                var input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) { Console.WriteLine("Please enter a value."); continue; }
                if (input.Trim().Equals("b", StringComparison.OrdinalIgnoreCase)) return null;

                if (int.TryParse(input, out int idx))
                {
                    if (idx >= 1 && idx <= materials.Count) return materials[idx - 1];
                    Console.WriteLine("Invalid number. Try again.");
                    continue;
                }

                if (_recipes.ContainsKey(input)) return input;

                var matches = materials
                    .Where(k => k.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(k => k.Length)
                    .Take(10)
                    .ToList();

                if (matches.Count == 0)
                {
                    Console.WriteLine($"Unknown material: \"{input}\". Try again.");
                    continue;
                }

                if (matches.Count == 1) return matches[0];

                Console.WriteLine("\nDid you mean:");
                for (int i = 0; i < matches.Count; i++)
                    Console.WriteLine($"  {i + 1}. {matches[i]}");
                var pick = PromptChoice("Pick one by number (or 'b' to go back): ",
                                        Enumerable.Range(1, matches.Count).Select(n => n.ToString()).Concat(new[] { "b" }).ToArray());
                if (pick.Equals("b", StringComparison.OrdinalIgnoreCase)) return null;
                return matches[int.Parse(pick, CultureInfo.InvariantCulture) - 1];
            }
        }

        static void PrintMaterials()
        {
            Console.WriteLine("Known materials:");
            foreach (var k in _recipes.Keys.OrderBy(k => k))
                Console.WriteLine(" - " + k);
        }

        // ---------- Core Calculation ----------
        static (double totalFocus, List<FocusLine> breakdown) CalculateFocus(string target, double unitsRequested, YieldMode mode)
        {
            var visitedStack = new Stack<string>();
            var lines = new List<FocusLine>();
            double focus = Recurse(target, unitsRequested, level: 0, lines, visitedStack, mode);
            return (focus, lines);
        }

        static double Recurse(string name, double unitsRequested, int level, List<FocusLine> lines, Stack<string> stack, YieldMode mode)
        {
            if (!_recipes.TryGetValue(name, out var r))
                throw new Exception($"Unknown material in graph: {name}");

            if (stack.Contains(name))
                throw new Exception($"Cycle detected while visiting: {string.Join(" -> ", stack.Reverse())} -> {name}");

            stack.Push(name);

            double yieldEff = EffectiveYield(r, mode);
            double craftsNeeded = Math.Ceiling(unitsRequested / Math.Max(1, yieldEff));
            double nodeFocus = craftsNeeded * r.FocusCost;
            double nodeTime  = craftsNeeded * Math.Max(0, r.TimePerCraftSeconds);

            string action = r.IsMineable
                ? (r.FocusCost > 0 ? "Mine" : "Gather")
                : (r.FocusCost > 0 ? "Craft" : "Craft");

            lines.Add(new FocusLine
            {
                Level = level,
                Action = action,
                Material = r.Name,
                Crafts = craftsNeeded,
                Yield = yieldEff,
                UnitsRequested = unitsRequested,
                FocusUsed = nodeFocus,
                TimeUsedSeconds = nodeTime
            });

            if (r.Ingredients is not null)
            {
                foreach (var kv in r.Ingredients)
                {
                    var ing = kv.Key;
                    var perCraft = kv.Value;
                    double needUnits = perCraft * craftsNeeded;
                    nodeFocus += Recurse(ing, needUnits, level + 1, lines, stack, mode);
                }
            }

            stack.Pop();
            return nodeFocus; // total time is aggregated from lines later
        }

        // ---------- Optimizer ----------
        static long MaxCraftable(string target, double availableFocus, YieldMode mode)
        {
            double oneUnitFocus = CalculateFocus(target, 1, mode).totalFocus;
            if (oneUnitFocus <= 0.0) return 0; // zero-cost chain

            long lo = 0, hi = 1;

            while (true)
            {
                double f = CalculateFocus(target, hi, mode).totalFocus;
                if (f > availableFocus) break;
                hi *= 2;
                if (hi > 1_000_000_000) break;
            }

            while (lo < hi)
            {
                long mid = lo + (hi - lo + 1) / 2;
                double f = CalculateFocus(target, mid, mode).totalFocus;
                if (f <= availableFocus) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }


        // ---------- CSV ----------
        static string ExportCsv(string target, double qty, List<FocusLine> lines, double totalFocus)
        {
            string safeTarget = string.Join("_", target.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string file = $"focus_breakdown_{safeTarget}_{qty}_{stamp}.csv";

            using var w = new StreamWriter(file);
            // Added TimePerCraftSeconds and a formatted time column
            w.WriteLine("Level,Action,Material,Crafts,Yield,UnitsRequested,FocusUsed,TimePerCraftSeconds,TimeUsedSeconds,TimeUsedFormatted");

            foreach (var ln in lines)
            {
                // Look up the recipe to get TimePerCraftSeconds
                _recipes.TryGetValue(ln.Material, out var r);
                double timePerCraft = r?.TimePerCraftSeconds ?? 0;

                string lineStr = string.Join(",",
                    ln.Level.ToString(CultureInfo.InvariantCulture),
                    Escape(ln.Action),
                    Escape(ln.Material),
                    ln.Crafts.ToString(CultureInfo.InvariantCulture),
                    ln.Yield.ToString(CultureInfo.InvariantCulture),
                    ln.UnitsRequested.ToString(CultureInfo.InvariantCulture),
                    ln.FocusUsed.ToString("F2", CultureInfo.InvariantCulture),
                    timePerCraft.ToString("F0", CultureInfo.InvariantCulture),
                    ln.TimeUsedSeconds.ToString("F0", CultureInfo.InvariantCulture),
                    Escape(FormatDuration(ln.TimeUsedSeconds))
                );

                w.WriteLine(lineStr);
            }

            double totalSec = SumTimeSeconds(lines);

            // Totals footer
            w.WriteLine(",,,,,,Total Focus," + totalFocus.ToString("F2", CultureInfo.InvariantCulture) + ",,");
            w.WriteLine(",,,,,,Total Time (seconds)," + totalSec.ToString("F0", CultureInfo.InvariantCulture) + "," + Escape(FormatDuration(totalSec)));

            return Path.GetFullPath(file);
        }


        // ---------- Prompt helpers ----------
        static string PromptChoice(string prompt, string[] allowed, string? defaultValue = null)
        {
            var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                Console.Write(prompt);
                var s = (Console.ReadLine() ?? "").Trim();
                if (string.IsNullOrEmpty(s) && defaultValue != null) return defaultValue;
                if (allowedSet.Contains(s)) return s;
                Console.WriteLine($"Invalid choice. Allowed: {string.Join("/", allowed)}");
            }
        }

        static bool PromptYesNo(string prompt)
        {
            var res = PromptChoice(prompt, new[] { "y", "n", "Y", "N" }, defaultValue: "n");
            return res.Equals("y", StringComparison.OrdinalIgnoreCase);
        }

        static double PromptDouble(string prompt, double? defaultValue = null, bool mustBeNonNegative = false)
        {
            while (true)
            {
                Console.Write(prompt);
                var s = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(s) && defaultValue.HasValue) return defaultValue.Value;

                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    if (mustBeNonNegative && d < 0)
                    {
                        Console.WriteLine("Please enter a non-negative number.");
                        continue;
                    }
                    return d;
                }
                Console.WriteLine("Invalid number. Try again.");
            }
        }

        static string PromptString(string prompt, bool allowEmpty = false)
        {
            while (true)
            {
                Console.Write(prompt);
                var s = Console.ReadLine() ?? "";
                if (!allowEmpty && string.IsNullOrWhiteSpace(s))
                {
                    Console.WriteLine("Please enter a value.");
                    continue;
                }
                return s.Trim();
            }
        }

        static string Escape(string s)
        {
            if (s.Contains(",") || s.Contains("\""))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        static void Pause(string msg = "\nPress Enter to return to the main menu...")
        {
            Console.Write(msg);
            Console.ReadLine();
        }

        // ---------- Cycle detection ----------
        static List<string>? FindCycle()
        {
            var color = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=unseen,1=visiting,2=done
            var path = new Stack<string>();

            foreach (var key in _recipes.Keys)
            {
                if (Dfs(key)) return path.Reverse().ToList();
            }
            return null;

            bool Dfs(string node)
            {
                if (!color.TryGetValue(node, out var c)) c = 0;
                if (c == 1) { path.Push(node); return true; }
                if (c == 2) return false;

                color[node] = 1;
                path.Push(node);

                if (_recipes.TryGetValue(node, out var r) && r.Ingredients is not null)
                {
                    foreach (var ing in r.Ingredients.Keys)
                    {
                        if (Dfs(ing)) return true;
                    }
                }

                path.Pop();
                color[node] = 2;
                return false;
            }
        }

        // ---------- Starter JSON (bootstrap) ----------
        static readonly string SampleRecipesJson = @"
        [
        {
            ""Name"": ""Mystery Metal"",
            ""FocusCost"": 10,
            ""Yield"": 1,
            ""TimePerCraftSeconds"": 5,
            ""IsMineable"": false,
            ""Ingredients"": { ""Boru Ore"": 8, ""Burning Powder"": 1 }
        },
        {
            ""Name"": ""Burning Powder"",
            ""FocusCost"": 20,
            ""Yield"": 15,
            ""TimePerCraftSeconds"": 5,
            ""IsMineable"": false,
            ""Ingredients"": { ""Charcoal"": 1 }
        },
        {
            ""Name"": ""Charcoal"",
            ""FocusCost"": 0,
            ""Yield"": 10,
            ""TimePerCraftSeconds"": 5,
            ""IsMineable"": false,
            ""Ingredients"": { ""Logs"": 28 }
        },
        {
            ""Name"": ""Boru Ore"",
            ""FocusCost"": 20,
            ""Yield"": 12,
            ""YieldMin"": 11,
            ""YieldMax"": 12,
            ""IsMineable"": true
        },
        {
            ""Name"": ""Logs"",
            ""FocusCost"": 0,
            ""Yield"": 1,
            ""IsMineable"": true
        }
        ]
        ";

    }
}
