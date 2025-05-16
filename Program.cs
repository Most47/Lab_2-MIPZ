// Основні імпорти .NET та Roslyn API
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Клас для збереження інформації про кожен клас у коді
class ClassInfo
{
    public string Name;
    public string? BaseClass;
    public List<string> Children = new();
    public int DIT;
    public int NOC => Children.Count;

    public int TotalMethods = 0;
    public int HiddenMethods = 0;
    public int InheritedMethods = 0;
    public int OverriddenMethods = 0;

    public int TotalFields = 0;
    public int HiddenFields = 0;
    public int InheritedFields = 0;

    public int DescendantCount = 0;

    public double MHF => TotalMethods == 0 ? 0 : (double)HiddenMethods / TotalMethods;
    public double AHF => TotalFields == 0 ? 0 : (double)HiddenFields / TotalFields;
    public double MIF => (TotalMethods + InheritedMethods) == 0 ? 0 : (double)InheritedMethods / (TotalMethods + InheritedMethods);
    public double AIF => (TotalFields + InheritedFields) == 0 ? 0 : (double)InheritedFields / (TotalFields + InheritedFields);
    public double POF =>
        TotalMethods > 0 && DescendantCount > 0
        ? (double)OverriddenMethods / (TotalMethods * DescendantCount)
        : 0;
}

class Program
{
    static Dictionary<string, ClassInfo> classes = new();

    static void Main()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string rootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ExternalCode", "src"));
        Console.WriteLine("Analyzing .cs files in: " + rootPath);

        var files = Directory
            .GetFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
                !f.Contains(@"\tests\") &&
                !f.Contains(@"\docs\") &&
                !f.Contains(@"\.git\") &&
                !f.Contains(@"\bin\") &&
                !f.Contains(@"\obj\") &&
                !Path.GetFileName(f).Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine("No .cs files found.");
            return;
        }

        Console.WriteLine($"Found {files.Count} .cs files");

        int total = files.Count, current = 0;

        foreach (var file in files)
        {
            current++;
            if (current % 10 == 0 || current == total)
                Console.Write($"\r{current}/{total} files processed... ({sw.Elapsed.TotalSeconds:F1}s)");

            try
            {
                var code = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetCompilationUnitRoot();

                foreach (var classNode in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var className = classNode.Identifier.Text;

                    var baseType = classNode.BaseList?.Types.FirstOrDefault()?.Type.ToString();


                    var rawBase = classNode.BaseList?.Types.FirstOrDefault()?.Type.ToString();

                    if (!classes.ContainsKey(className))
                        classes[className] = new ClassInfo { Name = className };

                    if (!string.IsNullOrEmpty(baseType))
                    {
                        classes[className].BaseClass = baseType;
                        if (!classes.ContainsKey(baseType))
                            classes[baseType] = new ClassInfo { Name = baseType };
                        classes[baseType].Children.Add(className);
                    }

                    var methods = classNode.Members.OfType<MethodDeclarationSyntax>();
                    foreach (var method in methods)
                    {
                        classes[className].TotalMethods++;

                        var modifiers = method.Modifiers.Select(m => m.Text).ToList();
                        if (modifiers.Contains("override") && !string.IsNullOrEmpty(baseType))
                        {
                            if (!classes.ContainsKey(baseType))
                                classes[baseType] = new ClassInfo { Name = baseType };

                            classes[baseType].OverriddenMethods++;
                        }
                    }


                    var fields = classNode.Members.OfType<FieldDeclarationSyntax>();
                    foreach (var field in fields)
                    {
                        classes[className].TotalFields++;
                        var modifiers = field.Modifiers.Select(m => m.Text).ToList();
                        if (modifiers.Contains("private") || modifiers.Contains("protected"))
                            classes[className].HiddenFields++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nError in {file}: {ex.Message}");
            }
        }

        Console.WriteLine("\nCalculating DIT...");
        foreach (var cls in classes.Values)
            cls.DIT = GetDIT(cls.Name);

        Console.WriteLine("Calculating inheritance...");
        foreach (var cls in classes.Values)
        {
            if (!string.IsNullOrEmpty(cls.BaseClass) && classes.ContainsKey(cls.BaseClass))
            {
                var baseCls = classes[cls.BaseClass];
                cls.InheritedMethods = baseCls.TotalMethods;
                cls.InheritedFields = baseCls.TotalFields;
            }
        }

        Console.WriteLine("Calculating descendant counts...");
        foreach (var cls in classes.Values)
            cls.DescendantCount = GetDescendantCount(cls.Name);

        Console.WriteLine("Generating CSV...");
        var output = "Class\tDIT\tNOC\tMHF (%)\tAHF (%)\tMIF (%)\tAIF (%)\tPOF (%)";

        foreach (var cls in classes.Values.OrderBy(c => c.Name))
        {
            output += $"\n{cls.Name}\t{cls.DIT}\t{cls.NOC}\t{cls.MHF * 100:F0}%\t{cls.AHF * 100:F0}%\t{cls.MIF * 100:F0}%\t{cls.AIF * 100:F0}%\t{cls.POF * 100:F0}%";
        }

        var totalCount = classes.Count;
        var avgDIT = classes.Values.Average(c => c.DIT);
        var maxDIT = classes.Values.Max(c => c.DIT);
        var totalNOC = classes.Values.Sum(c => c.NOC);
        var avgMHF = classes.Values.Average(c => c.MHF) * 100;
        var avgAHF = classes.Values.Average(c => c.AHF) * 100;
        var avgMIF = classes.Values.Average(c => c.MIF) * 100;
        var avgAIF = classes.Values.Average(c => c.AIF) * 100;
        var totalPossibleOverrides = classes.Values.Sum(c => c.TotalMethods * c.DescendantCount);
        var totalOverridden = classes.Values.Sum(c => c.OverriddenMethods);
        var globalPOF = totalPossibleOverrides == 0
                    ? 0
                    : (double)totalOverridden / totalPossibleOverrides * 100;

        output += $"\nTOTAL\t{avgDIT:F2}\t{totalNOC}\t{avgMHF:F0}%\t{avgAHF:F0}%\t{avgMIF:F0}%\t{avgAIF:F0}%\t{globalPOF:F0}%";

        string outputPath = @"Y:\magistracy_Univ\2\MIPZ\Prac_2\Prac_2\Output\metrics.csv";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllText(outputPath, output);

        sw.Stop();
        Console.WriteLine("Analysis complete. Results saved to Output/metrics.csv");
        Console.WriteLine($"Total classes processed: {classes.Count}");
        Console.WriteLine($"Total time: {sw.Elapsed.TotalSeconds:F1} seconds.");
    }

    static int GetDIT(string className)
    {
        int depth = 0;
        var visited = new HashSet<string>();
        var current = classes.GetValueOrDefault(className);

        while (current != null && !string.IsNullOrEmpty(current.BaseClass) && classes.ContainsKey(current.BaseClass))
        {
            if (!visited.Add(current.Name)) break;
            depth++;
            current = classes[current.BaseClass];
        }
        return depth;
    }

    static int GetDescendantCount(string className, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>();

        // Якщо вже бачили цей клас — зупиняємо рекурсію
        if (!visited.Add(className))
            return 0;

        var cls = classes[className];
        int count = 0;

        foreach (var child in cls.Children)
        {
            count += 1 + GetDescendantCount(child, visited);
        }

        return count;
    }

}
