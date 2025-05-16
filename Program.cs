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
    public string Name;                  // Ім'я класу
    public string? BaseClass;            // Ім'я базового класу (якщо є)
    public List<string> Children = new(); // Список прямих підкласів
    public int DIT;                      // Глибина в дереві успадкування
    public int NOC => Children.Count;    // Кількість дітей (Number of Children)

    // Лічильники методів
    public int TotalMethods = 0;         // Загальна кількість методів
    public int HiddenMethods = 0;        // Приватні/захищені методи
    public int InheritedMethods = 0;     // Успадковані методи від батька
    public int OverriddenMethods = 0;    // Методи, перевизначені в нащадках

    // Лічильники полів
    public int TotalFields = 0;          // Загальна кількість полів
    public int HiddenFields = 0;         // Приватні/захищені поля
    public int InheritedFields = 0;      // Успадковані поля від батька

    public int DescendantCount = 0;      // Загальна кількість всіх нащадків (всіх рівнів)

    // Метрики якості інкапсуляції
    public double MHF =>                   // Method Hiding Factor
        TotalMethods == 0 ? 0 : (double)HiddenMethods / TotalMethods;
    public double AHF =>                   // Attribute Hiding Factor
        TotalFields == 0 ? 0 : (double)HiddenFields / TotalFields;

    // Метрики використання успадкування
    public double MIF =>                   // Method Inheritance Factor
        (TotalMethods + InheritedMethods) == 0 ? 0 :
        (double)InheritedMethods / (TotalMethods + InheritedMethods);
    public double AIF =>                   // Attribute Inheritance Factor
        (TotalFields + InheritedFields) == 0 ? 0 :
        (double)InheritedFields / (TotalFields + InheritedFields);

    // Поліморфна фактор (усіх рівнів нащадків)
    public double POF =>                   // Polymorphism Factor
        TotalMethods > 0 && DescendantCount > 0
            ? (double)OverriddenMethods / (TotalMethods * DescendantCount)
            : 0;
}

class Program
{
    // Основне сховище для інформації про класи
    static Dictionary<string, ClassInfo> classes = new();

    static void Main()
    {
        // Запуск таймера для вимірювання продуктивності
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Шлях до папки з .cs файлами для аналізу
        string rootPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ExternalCode", "src")
        );
        Console.WriteLine("Analyzing .cs files in: " + rootPath);

        // Збір усіх .cs файлів (виключаємо тестові, docs, git, bin, obj)
        var files = Directory
            .GetFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
                !f.Contains("\\tests\\") &&
                !f.Contains("\\docs\\") &&
                !f.Contains("\\.git\\") &&
                !f.Contains("\\bin\\") &&
                !f.Contains("\\obj\\") &&
                !Path.GetFileName(f).Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine("No .cs files found.");
            return;  // Завершуємо, якщо файлів немає
        }

        Console.WriteLine($"Found {files.Count} .cs files");

        int total = files.Count, current = 0;

        // Обхід кожного файлу
        foreach (var file in files)
        {
            current++;
            // Виводимо прогрес кожні 10 файлів
            if (current % 10 == 0 || current == total)
                Console.Write($"\r{current}/{total} files processed... ({sw.Elapsed.TotalSeconds:F1}s)");

            try
            {
                // Парсинг коду з файлу
                var code = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetCompilationUnitRoot();

                // Знаходимо всі оголошення класів
                foreach (var classNode in root.DescendantNodes()
                                                 .OfType<ClassDeclarationSyntax>())
                {
                    var className = classNode.Identifier.Text;  // Ім'я класу
                    var baseType = classNode.BaseList?
                        .Types.FirstOrDefault()?.Type.ToString();  // Базовий клас

                    // Якщо клас новий — додаємо в словник
                    if (!classes.ContainsKey(className))
                        classes[className] = new ClassInfo { Name = className };

                    // Обробка успадкування
                    if (!string.IsNullOrEmpty(baseType))
                    {
                        classes[className].BaseClass = baseType;
                        if (!classes.ContainsKey(baseType))
                            classes[baseType] = new ClassInfo { Name = baseType };
                        classes[baseType].Children.Add(className); // Додаємо до дітей бази
                    }

                    // Аналіз методів у класі
                    var methods = classNode.Members.OfType<MethodDeclarationSyntax>();
                    foreach (var method in methods)
                    {
                        classes[className].TotalMethods++;
                        var modifiers = method.Modifiers.Select(m => m.Text).ToList();

                        // Лічимо перевизначення для базового класу
                        if (modifiers.Contains("override") && !string.IsNullOrEmpty(baseType))
                        {
                            if (!classes.ContainsKey(baseType))
                                classes[baseType] = new ClassInfo { Name = baseType };
                            classes[baseType].OverriddenMethods++;
                        }
                    }

                    // Аналіз полів у класі
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
                // Лог помилок, якщо не вдалося розпарсити файл
                Console.WriteLine($"\nError in {file}: {ex.Message}");
            }
        }

        // Обчислюємо DIT (глибину спадкування)
        Console.WriteLine("\nCalculating DIT...");
        foreach (var cls in classes.Values)
            cls.DIT = GetDIT(cls.Name);

        // Обчислюємо кількість успадкованих методів та полів
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

        // Обчислюємо кількість усіх нащадків
        Console.WriteLine("Calculating descendant counts...");
        foreach (var cls in classes.Values)
            cls.DescendantCount = GetDescendantCount(cls.Name);

        // Формуємо CSV-результат
        Console.WriteLine("Generating CSV...");
        var output = "Class\tDIT\tNOC\tMHF (%)\tAHF (%)\tMIF (%)\tAIF (%)\tPOF (%)";
        foreach (var cls in classes.Values.OrderBy(c => c.Name))
        {
            output += $"\n{cls.Name}\t{cls.DIT}\t{cls.NOC}\t{cls.MHF * 100:F0}%\t{cls.AHF * 100:F0}%\t{cls.MIF * 100:F0}%\t{cls.AIF * 100:F0}%\t{cls.POF * 100:F0}%";
        }

        // Підрахунок глобальних метрик
        var totalPossibleOverrides = classes.Values.Sum(c => c.TotalMethods * c.DescendantCount);
        var totalOverridden = classes.Values.Sum(c => c.OverriddenMethods);
        var globalPOF = totalPossibleOverrides == 0
            ? 0
            : (double)totalOverridden / totalPossibleOverrides * 100;
        output += $"\nTOTAL\t{classes.Values.Average(c => c.DIT):F2}\t{classes.Values.Sum(c => c.NOC)}\t{classes.Values.Average(c => c.MHF) * 100:F0}%\t{classes.Values.Average(c => c.AHF) * 100:F0}%\t{classes.Values.Average(c => c.MIF) * 100:F0}%\t{classes.Values.Average(c => c.AIF) * 100:F0}%\t{globalPOF:F0}%";

        // Запис у файл
        string outputPath = @"Y:\magistracy_Univ\2\MIPZ\Prac_2\Prac_2\Output\metrics.csv";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllText(outputPath, output);

        sw.Stop();
        Console.WriteLine("Analysis complete. Results saved to Output/metrics.csv");
        Console.WriteLine($"Total classes processed: {classes.Count}");
        Console.WriteLine($"Total time: {sw.Elapsed.TotalSeconds:F1} seconds.");
    }

    // Рекурсивна функція для обчислення DIT
    static int GetDIT(string className)
    {
        int depth = 0;
        var visited = new HashSet<string>();
        var current = classes.GetValueOrDefault(className);
        while (current != null && !string.IsNullOrEmpty(current.BaseClass) && classes.ContainsKey(current.BaseClass))
        {
            if (!visited.Add(current.Name)) break; // захист від циклів
            depth++;
            current = classes[current.BaseClass];
        }
        return depth;
    }

    // Рекурсивна функція для підрахунку всіх нащадків
    static int GetDescendantCount(string className, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>();
        if (!visited.Add(className)) return 0;  // уникаємо зациклення
        var cls = classes[className];
        int count = 0;
        foreach (var child in cls.Children)
            count += 1 + GetDescendantCount(child, visited);
        return count;
    }
}