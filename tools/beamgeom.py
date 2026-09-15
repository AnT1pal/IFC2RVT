"""
Answers one question: what shape does the file actually carry for a member whose designation says
it is an angle, a tube or a plate?

It matters before deciding anything about families. If the swept area really is an L-profile, then
a wrong family is the only thing lost and loading the right one fixes everything. If the file only
carries a bounding rectangle, then the true section is not in the file at all, and no amount of
family loading will recover it - the honest ceiling is lower than it looks.

    python tools/beamgeom.py model.ifc
"""

import collections
import re
import sys

STEP_UNICODE = re.compile(r"\\X2\\([0-9A-Fa-f]+)\\X0\\")
ENTITY = re.compile(r"^#(\d+)=(IFC\w+)\((.*)$")
REFS = re.compile(r"#(\d+)")

STRUCTURAL = {"IFCBEAM", "IFCMEMBER", "IFCCOLUMN", "IFCPLATE"}


def decode(text):
    def replace(match):
        digits = match.group(1)
        return "".join(chr(int(digits[i:i + 4], 16)) for i in range(0, len(digits), 4))
    return STEP_UNICODE.sub(replace, text)


def split_attributes(body):
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
        return decode(value[1:-1]).strip() or None
    return None


def main():
    if len(sys.argv) < 2:
        print("usage: python tools/beamgeom.py <file.ifc>")
        return 2

    path = sys.argv[1]

    entities = {}          # label -> (kind, raw body)
    members = []           # (label, kind, description, representation ref)

    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            match = ENTITY.match(line.strip())
            if not match:
                continue

            label, kind, body = match.group(1), match.group(2).upper(), match.group(3)
            entities[label] = (kind, body)

            if kind in STRUCTURAL:
                attributes = split_attributes(body)
                description = unquote(attributes[3]) if len(attributes) > 3 else None
                representation = None
                if len(attributes) > 6:
                    found = REFS.findall(attributes[6])
                    representation = found[0] if found else None
                if description and representation:
                    members.append((label, kind, description, representation))

    def follow(label, depth=0):
        """Walks a representation down to the swept area, reporting what it finds."""
        if depth > 12 or label not in entities:
            return None
        kind, body = entities[label]

        if kind.endswith("PROFILEDEF"):
            return kind

        if kind == "IFCEXTRUDEDAREASOLID":
            profile = REFS.findall(split_attributes(body)[0])
            return follow(profile[0], depth + 1) if profile else "IFCEXTRUDEDAREASOLID(?)"

        if kind in ("IFCBOOLEANCLIPPINGRESULT", "IFCBOOLEANRESULT"):
            operands = split_attributes(body)
            refs = REFS.findall(operands[1]) if len(operands) > 1 else []
            return follow(refs[0], depth + 1) if refs else None

        if kind in ("IFCPRODUCTDEFINITIONSHAPE", "IFCSHAPEREPRESENTATION",
                    "IFCMAPPEDITEM", "IFCREPRESENTATIONMAP"):
            for ref in REFS.findall(body):
                result = follow(ref, depth + 1)
                if result:
                    return result
            return None

        if kind.endswith("BREP") or "FACESET" in kind or "SURFACEMODEL" in kind:
            return kind

        return None

    shapes = collections.defaultdict(collections.Counter)
    for _, _, description, representation in members:
        shapes[description][follow(representation) or "(не прослежено)"] += 1

    print(f"Файл: {path}")
    print(f"Конструктивных элементов с обозначением: {len(members)}")
    print()
    print(f"{'элементов':>10}  {'обозначение':<24} что лежит в геометрии")
    print("-" * 92)

    ordered = sorted(shapes.items(), key=lambda kv: -sum(kv[1].values()))
    for designation, kinds in ordered[:24]:
        total = sum(kinds.values())
        detail = ", ".join(f"{k[3:]}={v}" for k, v in kinds.most_common(3))
        print(f"{total:>10}  {designation[:23]:<24} {detail}")

    print()
    print("=== ИТОГ ПО ФОРМАМ СЕЧЕНИЙ ===")
    overall = collections.Counter()
    for kinds in shapes.values():
        overall.update(kinds)

    total = sum(overall.values())
    for kind, count in overall.most_common():
        share = 100.0 * count / total if total else 0
        print(f"  {count:>7} ({share:5.1f}%)  {kind}")

    print()
    honest = sum(c for k, c in overall.items()
                 if k.startswith(("IFCL", "IFCI", "IFCU", "IFCT", "IFCC", "IFCZ")) and k.endswith("PROFILEDEF"))
    boxes = overall.get("IFCRECTANGLEPROFILEDEF", 0) + overall.get("IFCARBITRARYCLOSEDPROFILEDEF", 0)
    print(f"  Сечений настоящей формы (L, I, U, T, круг): {honest}")
    print(f"  Прямоугольников и произвольных контуров:    {boxes}")
    if boxes > honest:
        print()
        print("  Значит форма сечения в файле в основном НЕ параметрическая:")
        print("  загрузка семейств вернёт правильный тип, но исходная геометрия")
        print("  всё равно была упрощена при выгрузке.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
