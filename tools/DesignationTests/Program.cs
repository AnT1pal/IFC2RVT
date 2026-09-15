using System;
using System.Collections.Generic;
using System.Text;
using IFC2RVT.Mapping;

namespace IFC2RVT.Tests
{
    /// <summary>
    /// Checks the steel designation rules against the spellings that actually occur in a real
    /// Russian model, rather than against invented ones.
    ///
    /// Every case below was taken from the catalogue extracted out of a real model: 45 designations
    /// over 3344 members. Two spellings pull in opposite directions there, and both have to be got
    /// right. "Гн[160Х60Х4" is written with Cyrillic Х while "Гнз120X60X4" uses Latin X, so a
    /// converter comparing raw strings fails to find a family sitting in the project. Yet folding
    /// too hard is worse: "Гн[" and "Гнз" differ by an open versus a closed section, the model
    /// contains a 60x40x3 in each, and merging them hands 284 closed tubes a channel's outline.
    /// </summary>
    internal static class Program
    {
        static int _passed;
        static int _failed;

        static int Main()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            Section("одно сечение, разные написания — должны совпасть");
            Same("Гн[160Х60Х4", "Гн[160x60x4", true, "тот же швеллер: кириллическая Х против латинской X");
            Same("Гнз120X60X4", "Гн120х60х4", true, "замкнутый профиль с суффиксом з и без");
            Same("L50X3", "L50x3", true, "регистр разделителя");
            Same("I30М", "I30M", true, "кириллическая М");
            Same("Гнз60X40X3", "Гн60x40x3", true, "префикс с суффиксом и без");
            Same("-10*150", "-10x150", true, "звёздочка против x");
            Same("I20Б1", "I 20Б1", true, "пробел после серии");
            Same("L50X3", "L 50 x 3", true, "пробелы вокруг разделителей");
            Same("Гнз120X120X4", "Гн 120х120х4", true, "префикс и пробелы вместе");

            Section("разные сечения — склеиваться не должны");
            Same("L50X3", "L63X5", false, "разные уголки");
            Same("Гнз120X60X4", "Гнз120X120X4", false, "разные гнутые");
            Same("I20Б1", "I25Б1", false, "разные двутавры");
            Same("-10*150", "-12*150", false, "разная толщина листа");
            Same("Гнз100X50X4", "Гнз100X100X4", false, "разная ширина");
            Same("I20Б1", "I 20Н1", false, "серия Б против Н - разные профили");
            Same("L50X3", "L50X4", false, "разная толщина полки");

            // Проверено по контурам в файле: у "Гн[" площадь сечения равна замкнутой трубе
            // минус ровно одна стенка (1088 против 1696 у 160x60x4, 402 против 564 у 60x40x3).
            // "Гн[" - гнутый швеллер, "Гнз" - замкнутый профиль, и в модели есть оба размера 60x40x3.
            Same("Гн[60Х40Х3", "Гнз60X40X3", false, "гнутый швеллер против замкнутого профиля");
            Same("Гн[160Х60Х4", "Гнз160X60X4", false, "открытое сечение против закрытого");
            Same("Гн[120Х50Х4", "Гн120x50x4", false, "скобка - это швеллер, а не сокращение");

            Section("вид проката по ГОСТ");
            Kind("L50X3", SteelKind.Angle);
            Kind("L100X63X6", SteelKind.Angle);
            Kind("L140X9", SteelKind.Angle);
            Kind("Гнз120X120X4", SteelKind.HollowSection);
            Kind("Гн140X60X4", SteelKind.HollowSection);
            Kind("Гн[160Х60Х4", SteelKind.ColdFormedChannel);
            Kind("Гн[60Х40Х3", SteelKind.ColdFormedChannel);
            Kind("I20Б1", SteelKind.IBeam);
            Kind("I25K1", SteelKind.IBeam);
            Kind("ВГП DN20X2.8", SteelKind.Tube);
            Kind("ф18", SteelKind.Round);
            Kind("PL1250*3", SteelKind.Strip);
            Kind("-10*150", SteelKind.Sheet);
            Kind("-12*110", SteelKind.Sheet);
            Kind("рифл. t=4, B=900", SteelKind.CheckerPlate);
            Kind("—ПВ506X700", SteelKind.Unknown);

            Section("плоский прокат — это не несущая конструкция");
            Flat("-10*150", true);
            Flat("PL1250*3", true);
            Flat("рифл. t=4, B=900", true);
            Flat("L50X3", false);
            Flat("Гнз120X120X4", false);
            Flat("Гн[160Х60Х4", false);
            Flat("I20Б1", false);

            Console.WriteLine();
            if (_failed == 0)
            {
                Console.WriteLine($"Все проверки пройдены: {_passed}");
                return 0;
            }

            Console.WriteLine($"Пройдено {_passed}, ПРОВАЛЕНО {_failed}");
            return 1;
        }

        static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("=== " + title + " ===");
        }

        static void Same(string a, string b, bool expected, string why)
        {
            var actual = SteelDesignation.SameSection(a, b);
            Report(actual == expected,
                   $"{a,-16} {b,-16} -> {SteelDesignation.Normalise(a),-14} {SteelDesignation.Normalise(b),-14} {why}");
        }

        static void Kind(string text, SteelKind expected)
        {
            var parsed = SteelDesignation.Parse(text);
            Report(parsed.Kind == expected,
                   $"{text,-22} {parsed.Kind,-16} {parsed.Standard}");
        }

        static void Flat(string text, bool expected)
        {
            var parsed = SteelDesignation.Parse(text);
            Report(parsed.IsFlatProduct == expected,
                   $"{text,-22} плоский={parsed.IsFlatProduct,-6} {parsed.KindName}");
        }

        static void Report(bool ok, string line)
        {
            if (ok) _passed++; else _failed++;
            Console.WriteLine((ok ? "  ok   " : "  ПРОВАЛ ") + line);
        }
    }
}
