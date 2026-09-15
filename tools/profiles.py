"""
Pulls the structural profile catalogue straight out of an IFC file.

The point is ground truth: every distinct section the model asks for, where the designation was
written, and how many members use it. That answers the one question a converter cannot answer on
its own - which families a project must have loaded before native framing means anything - and it
answers it from the file in hand rather than from a table off the internet.

Two sources, because exporters disagree about which to use:

  IfcMaterialProfileSet   the proper place - a parametric profile with a name and dimensions
  Description             loose text on the element, which is where Revit actually writes the
                          section when steel is modelled with generic framing

On the model this was built against the second source carries everything: 2613 beams and three
IfcMaterialProfileSetUsage between them.

Runs on the raw STEP text, so it needs nothing installed and copes with files too large to open.

    python tools/profiles.py model.ifc
"""

import collections
import re
import sys

STEP_UNICODE = re.compile(r"\\X2\\([0-9A-Fa-f]+)\\X0\\")
ENTITY = re.compile(r"^#(\d+)=(IFC\w+)\((.*)$")

STRUCTURAL = {"IFCBEAM", "IFCMEMBER", "IFCCOLUMN", "IFCPLATE",
              "IFCBEAMSTANDARDCASE", "IFCMEMBERSTANDARDCASE", "IFCCOLUMNSTANDARDCASE"}

# Cyrillic letters drawn identically to a Latin one. A designation written with Cyrillic Х and the
# same designation written with Latin X are different strings that no name match will ever join -
# and this file contains both spellings of the same kind of section.
HOMOGLYPHS = str.maketrans({
    "А": "A", "В": "B", "Е": "E", "К": "K", "М": "M", "Н": "H", "О": "O",
    "Р": "P", "С": "C", "Т": "T", "У": "Y", "Х": "X",
})

# Designation prefixes used by Russian steel standards. Knowing which family a designation belongs
# to is what tells a converter where to look: an angle is not an I-beam, however alike the strings.
GOST_KINDS = [
    (re.compile(r"^L\s*\d"), "уголок", "ГОСТ 8509-93 / 8510-86"),
    (re.compile(r"^Гн[зc]?\s*\[?\s*\d", re.I), "гнутый замкнутый", "ГОСТ 30245-2003"),
    (re.compile(r"^\[\s*\d"), "швеллер", "ГОСТ 8240-97"),
    (re.compile(r"^(I|Б|Ш|К|Д)\s*\d"), "двутавр", "ГОСТ 26020-83 / 8239-89"),
    (re.compile(r"^(PL|ПЛ)\s*\d", re.I), "полоса", "ГОСТ 103-2006"),
    (re.compile(r"^-\s*\d+\s*[*xх]"), "лист", "ГОСТ 19903-2015"),
    (re.compile(r"^риф", re.I), "рифлёный лист", "ГОСТ 8568-77"),
    (re.compile(r"^(D|Ø|Труб)\s*\d", re.I), "труба", "ГОСТ 8732-78 / 10704-91"),
]

DESIGNATION = re.compile(r"^[A-Za-zА-Яа-яØ\[\-]{0,12}\s*\d")


def decode(text):
    """Decodes the \\X2\\....\\X0\\ escapes STEP uses for non-ASCII."""
    def replace(match):
        digits = match.group(1)
        return "".join(chr(int(digits[i:i + 4], 16)) for i in range(0, len(digits), 4))
    return STEP_UNICODE.sub(replace, text)


def split_attributes(body):
    """Splits a STEP attribute list, ignoring commas inside strings and nested parentheses."""
    parts, depth, current, in_string = [], 0, [], False

    for ch in body:
        if in_string:
            current.append(ch)
            if ch == "'":
                in_string = False
            continue
        if ch == "'":
            in_string = True
            current.append(ch)
        elif ch == "(":
            depth += 1
            current.append(ch)
        elif ch == ")":
            if depth == 0:
                break
            depth -= 1
            current.append(ch)
        elif ch == "," and depth == 0:
            parts.append("".join(current).strip())
            current = []
        else:
            current.append(ch)

    parts.append("".join(current).strip())
    return parts


def unquote(value):
    value = (value or "").strip()
    if value in ("$", "*", ""):
        return None
    if value.startswith("'") and value.endswith("'"):
        text = decode(value[1:-1]).strip()
        return text or None
    return None


def number(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def dimensions(kind, attributes):
    """Named dimensions per profile kind, in file units."""
    values = [unquote(x) if x.strip().startswith("'") else x for x in attributes]

    def at(i):
        return number(values[i]) if i < len(values) else None

    table = {
        "IFCISHAPEPROFILEDEF": lambda: {"b": at(3), "h": at(4), "s": at(5), "t": at(6)},
        "IFCLSHAPEPROFILEDEF": lambda: {"h": at(3), "b": at(4), "t": at(5)},
        "IFCUSHAPEPROFILEDEF": lambda: {"h": at(3), "b": at(4), "s": at(5), "t": at(6)},
        "IFCTSHAPEPROFILEDEF": lambda: {"h": at(3), "b": at(4), "s": at(5), "t": at(6)},
        "IFCRECTANGLEPROFILEDEF": lambda: {"x": at(3), "y": at(4)},
        "IFCRECTANGLEHOLLOWPROFILEDEF": lambda: {"x": at(3), "y": at(4), "t": at(5)},
        "IFCCIRCLEPROFILEDEF": lambda: {"d": (at(3) or 0) * 2 or None},
        "IFCCIRCLEHOLLOWPROFILEDEF": lambda: {"d": (at(3) or 0) * 2 or None, "t": at(4)},
    }
    return table.get(kind, dict)()


def classify(name):
    if not name:
        return "(без обозначения)", ""
    normalised = name.translate(HOMOGLYPHS).strip()
    for pattern, kind, standard in GOST_KINDS:
        if pattern.match(normalised):
            return kind, standard
    return "не по ГОСТ", ""


def mixes_scripts(text):
    """
    True when a technical designation carries a Cyrillic letter that looks Latin. Only applied to
    things shaped like designations: running Russian prose is full of these letters legitimately,
    and flagging it would bury the finding that matters.
    """
    if not text or not DESIGNATION.match(text.translate(HOMOGLYPHS)):
        return False
    return any(ch in "АВЕКМНОРСТУХ" for ch in text)


def main():
    if len(sys.argv) < 2:
        print("usage: python tools/profiles.py <file.ifc>")
        return 2

    path = sys.argv[1]
    declared = {}                       # IfcProfileDef -> name and dimensions
    from_description = collections.Counter()
    by_entity = collections.defaultdict(collections.Counter)

    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            match = ENTITY.match(line.strip())
            if not match:
                continue

            kind = match.group(2).upper()

            if kind.endswith("PROFILEDEF"):
                attributes = split_attributes(match.group(3))
                name = unquote(attributes[1]) if len(attributes) > 1 else None
                declared[match.group(1)] = {"kind": kind, "name": name,
                                            "dims": dimensions(kind, attributes)}
                continue

            if kind in STRUCTURAL:
                attributes = split_attributes(match.group(3))
                description = unquote(attributes[3]) if len(attributes) > 3 else None
                if description and description.lower() != "main piece":
                    from_description[description] += 1
                    by_entity[kind][description] += 1

    named = collections.Counter()
    dims_of = {}
    for info in declared.values():
        if info["name"]:
            named[info["name"]] += 1
            if info["dims"]:
                dims_of[info["name"]] = info["dims"]

    print(f"Файл: {path}")
    print(f"IfcProfileDef с именем: {len(named)} обозначений")
    print(f"Обозначений в Description конструктивных элементов: {len(from_description)}")
    print()

    if not from_description:
        print("В Description обозначений нет - профили пришлось бы искать только по IfcProfileDef.")
    else:
        print("=== СЕЧЕНИЯ, КОТОРЫЕ ТРЕБУЕТ МОДЕЛЬ ===")
        print(f"{'элементов':>10}  {'вид':<20} {'обозначение':<26} стандарт")
        print("-" * 96)

        summary = collections.Counter()
        mixed = []

        for name, count in from_description.most_common():
            kind, standard = classify(name)
            summary[(kind, standard)] += count
            if mixes_scripts(name):
                mixed.append(name)
            print(f"{count:>10}  {kind:<20} {name[:25]:<26} {standard}")

        print()
        print("=== СКОЛЬКО СЕМЕЙСТВ REVIT НУЖНО ЗАГРУЗИТЬ ===")
        total = sum(summary.values())
        for (kind, standard), count in summary.most_common():
            share = 100.0 * count / total if total else 0
            print(f"  {count:>6} элементов ({share:5.1f}%)  {kind:<20} {standard}")

        if mixed:
            print()
            print(f"=== ОБОЗНАЧЕНИЯ СО СМЕШАННЫМИ АЛФАВИТАМИ: {len(mixed)} ===")
            print("  Кириллические буквы, неотличимые от латинских. Поиск типоразмера по имени")
            print("  на таких обозначениях промахивается молча.")
            for name in mixed[:12]:
                cyr = "".join(ch for ch in name if ch in "АВЕКМНОРСТУХ")
                print(f"    {name:<32} кириллица: {cyr}")

        print()
        print("=== ПО ТИПАМ ЭЛЕМЕНТОВ ===")
        for entity, counter in sorted(by_entity.items(), key=lambda kv: -sum(kv[1].values())):
            print(f"  {entity:<24} {sum(counter.values()):>6} элементов, "
                  f"{len(counter):>3} обозначений")

    return 0


if __name__ == "__main__":
    sys.exit(main())
