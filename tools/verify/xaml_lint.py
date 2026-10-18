#!/usr/bin/env python3
"""
Static checks for the WPF XAML in src/MilestoneAdmin.UI (complements the C# compile check on systems without the WPF
markup compiler):

  * every file is well-formed XML
  * x:Class has a matching partial class; event handlers referenced in XAML exist in it
  * every {StaticResource key} is defined (theme, App resources or the file's own resources)
  * {Binding Path} resolves to a public property of the data context type, inferred from d:DataContext,
    DataTemplate DataType, DataContext="{Binding X}", RelativeSource AncestorType, and the element type of ItemsSource
    collections (for item templates and DataGrid columns). Paths into types the linter cannot see are accepted.

Usage: python3 xaml_lint.py <repo-root>   (exit code 1 when problems are found)
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

X = "http://schemas.microsoft.com/winfx/2006/xaml"
D = "http://schemas.microsoft.com/expression/blend/2008"
WPF = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"

COLLECTION_RE = re.compile(r"^(?:System\.Collections\.(?:ObjectModel\.|Generic\.)?)?(ObservableCollection|List|IEnumerable|IReadOnlyList|IReadOnlyCollection|IList|ICollection)<(.+)>$")


def find_matching(text, i):
    depth = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            i = j
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return n


def load_types(src_root):
    """class name -> {"base": name, "props": {name: type}, "methods": set()}"""
    types = {}
    for dirpath, _, files in os.walk(src_root):
        if os.sep + "obj" in dirpath or os.sep + "bin" in dirpath:
            continue
        for fn in files:
            if not fn.endswith(".cs"):
                continue
            text = open(os.path.join(dirpath, fn), encoding="utf-8").read()
            for m in re.finditer(r"\b(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*(?:class|struct|interface)\s+(\w+)(?:<[^>]*>)?\s*(?::\s*([\w.<>, ]+?))?\s*(?:where[^{]*)?\{", text):
                name = m.group(1)
                base = None
                if m.group(2):
                    base = m.group(2).split(",")[0].strip().split("<")[0].split(".")[-1]
                start = m.end() - 1
                end = find_matching(text, start)
                body = text[start:end]
                t = types.setdefault(name, {"base": base, "props": {}, "methods": set()})
                if base and not t["base"]:
                    t["base"] = base
                for pm in re.finditer(r"\bpublic\s+(?:override\s+|virtual\s+|static\s+|new\s+|abstract\s+)*([\w.]+(?:<[^;=(){}]*?>)?(?:\[\])?\??)\s+(\w+)\s*(?:\{|=>)", body):
                    t["props"].setdefault(pm.group(2), pm.group(1).strip())
                for mm in re.finditer(r"\b(?:void|async\s+void|Task)\s+(\w+)\s*\(", body):
                    t["methods"].add(mm.group(1))
    return types


def strip_generic(t):
    t = (t or "").strip().rstrip("?")
    m = COLLECTION_RE.match(t)
    if m:
        return None
    return t.split("<")[0].split(".")[-1]


def element_type(t):
    t = (t or "").strip()
    m = COLLECTION_RE.match(t)
    if m:
        return m.group(2).strip().split(".")[-1]
    if t.endswith("[]"):
        return t[:-2].split(".")[-1]
    return None


class Linter:
    def __init__(self, root):
        self.root = root
        self.src = os.path.join(root, "src")
        self.ui = os.path.join(self.src, "MilestoneAdmin.UI")
        self.types = load_types(self.src)
        self.problems = []
        self.global_keys = set()
        for f in ("Themes/Theme.xaml", "App.xaml", "Views/PageTemplates.xaml"):
            p = os.path.join(self.ui, f)
            if os.path.exists(p):
                for el in ET.parse(p).getroot().iter():
                    k = el.get("{%s}Key" % X)
                    if k:
                        self.global_keys.add(k)

    def prop_type(self, cls, prop):
        seen = set()
        while cls and cls in self.types and cls not in seen:
            seen.add(cls)
            if prop in self.types[cls]["props"]:
                return True, self.types[cls]["props"][prop]
            cls = self.types[cls]["base"]
        # Reaching a framework interface (e.g. ObservableObject : INotifyPropertyChanged) means the chain is exhausted.
        if cls in ("ObservableObject", "Object", None) or re.match(r"^I[A-Z]\w*$", cls or ""):
            return False, None
        return None, None  # unknown base outside our sources: cannot judge

    def known(self, cls):
        return cls in self.types

    def resolve_path(self, cls, path, where):
        """Checks a binding path; returns the type of the final segment (or None when unknown)."""
        if cls is None or not self.known(cls):
            return None
        segments = [s for s in re.split(r"\.(?![^\[]*\])", path) if s]
        current = cls
        for seg in segments:
            name = seg.split("[")[0]
            if name in ("Count",) and current is None:
                return None
            if not current or not self.known(current):
                return None
            ok, t = self.prop_type(current, name)
            if ok is False:
                self.problems.append(f"{where}: '{path}' - '{name}' is not a public property of {current}")
                return None
            if ok is None:
                return None
            if "[" in seg:
                return None
            current = strip_generic(t)
            last_full = t
        return last_full if segments else None

    @staticmethod
    def parse_binding(value):
        m = re.match(r"^\{Binding(?:\s+(.*))?\}$", value.strip(), flags=re.S)
        if not m:
            return None
        inner = m.group(1) or ""
        parts = []
        depth = 0
        buf = ""
        for ch in inner:
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
            if ch == "," and depth == 0:
                parts.append(buf.strip())
                buf = ""
            else:
                buf += ch
        if buf.strip():
            parts.append(buf.strip())
        info = {"path": "", "relative": None, "element": None}
        for i, p in enumerate(parts):
            if "=" in p and not p.startswith("{"):
                k, v = p.split("=", 1)
                k = k.strip()
                if k == "Path":
                    info["path"] = v.strip()
                elif k == "RelativeSource":
                    am = re.search(r"AncestorType=([\w:]+)", v)
                    info["relative"] = am.group(1).split(":")[-1] if am else "Self"
                elif k == "ElementName":
                    info["element"] = v
                elif k == "Source":
                    info["element"] = "source"
            elif i == 0:
                info["path"] = p
        return info

    def resolve_xtype(self, value):
        m = re.search(r"\{x:Type\s+(?:\w+:)?([\w+]+)\}", value or "") or re.search(r"Type=(?:\w+:)?([\w+]+)", value or "")
        return m.group(1).split("+")[-1] if m else None

    def lint_file(self, path):
        rel = os.path.relpath(path, self.root)
        try:
            tree = ET.parse(path)
        except ET.ParseError as e:
            self.problems.append(f"{rel}: not well-formed XML: {e}")
            return
        root = tree.getroot()
        cls = root.get("{%s}Class" % X)
        local_keys = {el.get("{%s}Key" % X) for el in root.iter() if el.get("{%s}Key" % X)}
        code_cls = None
        if cls:
            code_cls = cls.split(".")[-1]
            if code_cls not in self.types:
                self.problems.append(f"{rel}: x:Class {cls} has no partial class in the C# sources")
        root_type = self.resolve_xtype(root.get("{%s}DataContext" % D))
        if root_type and not self.known(root_type):
            self.problems.append(f"{rel}: d:DataContext type {root_type} not found")
        parent = {c: p for p in root.iter() for c in p}

        def ancestors(el):
            while el in parent:
                el = parent[el]
                yield el

        scope = {}

        def walk(el, ctx, item_ctx):
            tag = el.tag.split("}")[-1]
            here = ctx
            # DataTemplate with DataType -> typed scope; without -> item type of the owning collection, else unknown
            if tag in ("DataTemplate", "HierarchicalDataTemplate"):
                dt = self.resolve_xtype(el.get("DataType"))
                here = dt if dt else item_ctx
            if tag == "ControlTemplate":
                here = ctx
            for attr, value in el.attrib.items():
                aname = attr.split("}")[-1]
                if attr.startswith("{%s}" % D) or attr.startswith("{%s}" % X):
                    continue
                if aname.endswith("Changed") or aname in ("Click", "Loaded", "Checked", "Unchecked"):
                    if code_cls and code_cls in self.types and value not in self.types[code_cls]["methods"] and not value.startswith("{"):
                        self.problems.append(f"{rel}: event handler '{value}' not found in {code_cls}")
                for sm in re.finditer(r"\{(?:StaticResource|DynamicResource)\s+([^{}\s]+)\}", value):
                    key = sm.group(1)
                    if key not in self.global_keys and key not in local_keys:
                        self.problems.append(f"{rel}: resource key '{key}' is not defined")
            scope[el] = here
            # DataContext rebinding for this element and its children
            child_ctx = here
            dc = el.get("DataContext")
            if dc:
                b = self.parse_binding(dc)
                if b and not b["relative"] and not b["element"]:
                    t = self.resolve_path(here, b["path"], f"{rel} <{tag} DataContext>") if b["path"] else None
                    child_ctx = strip_generic(t) if t else (here if not b["path"] else None)
            # check bindings on this element (DataContext attribute is evaluated in the parent context, others in child_ctx)
            for attr, value in el.attrib.items():
                aname = attr.split("}")[-1]
                if aname == "DataContext" or attr.startswith("{%s}" % D):
                    continue
                for bm in re.finditer(r"\{Binding[^{}]*(?:\{[^{}]*\}[^{}]*)*\}", value):
                    b = self.parse_binding(bm.group(0))
                    if not b or b["element"]:
                        continue
                    path = b["path"]
                    if not path or path.startswith("("):
                        continue
                    if b["relative"] and b["relative"] != "Self":
                        anc = next((a for a in ancestors(el) if a.tag.split("}")[-1] == b["relative"]), None)
                        if anc is None and b["relative"] in ("UserControl", "Window") and root.tag.split("}")[-1] == b["relative"]:
                            anc = root
                        if anc is None:
                            continue
                        anc_ctx = scope.get(anc, root_type)
                        if anc is root:
                            anc_ctx = root_type
                        if path.startswith("DataContext."):
                            self.resolve_path(anc_ctx, path[len("DataContext."):], f"{rel} <{tag} {aname}>")
                        continue
                    if b["relative"] == "Self":
                        continue
                    target_ctx = child_ctx
                    if tag in ("DataGridTextColumn", "DataGridCheckBoxColumn", "DataGridComboBoxColumn"):
                        target_ctx = item_ctx
                    self.resolve_path(target_ctx, path, f"{rel} <{tag} {aname}>")
            # Items type for children (templates, columns, DisplayMemberPath)
            next_item = item_ctx if (tag.startswith("DataGrid") and tag.endswith("Column")) or "." in tag else None
            src = el.get("ItemsSource")
            if src:
                b = self.parse_binding(src)
                if b and not b["relative"] and not b["element"] and b["path"]:
                    t = self.resolve_path(child_ctx, b["path"], f"{rel} <{tag} ItemsSource>")
                    next_item = element_type(t) if t else None
                    dmp = el.get("DisplayMemberPath")
                    if dmp and next_item:
                        self.resolve_path(next_item, dmp, f"{rel} <{tag} DisplayMemberPath>")
            # inside a DataGrid column (ElementStyle, CellTemplate, ...) the data context is the row item
            if tag.startswith("DataGrid") and tag.endswith("Column"):
                child_ctx = item_ctx
            for c in el:
                ctag = c.tag.split("}")[-1]
                # property elements (e.g. DataGrid.Columns, ItemsControl.ItemTemplate) keep the owner's item type
                if "." in ctag:
                    walk(c, child_ctx, next_item if next_item is not None else item_ctx)
                else:
                    walk(c, child_ctx, next_item if next_item is not None else (item_ctx if tag.endswith("Column") or tag in ("DataTemplate",) else None))

        walk(root, root_type, None)

    def run(self):
        count = 0
        for dirpath, _, files in os.walk(self.ui):
            if os.sep + "obj" in dirpath or os.sep + "bin" in dirpath:
                continue
            for fn in sorted(files):
                if fn.endswith(".xaml"):
                    count += 1
                    self.lint_file(os.path.join(dirpath, fn))
        for p in self.problems:
            print("XAML:", p)
        print(f"Checked {count} XAML files: {len(self.problems)} problem(s).")
        return 1 if self.problems else 0


if __name__ == "__main__":
    sys.exit(Linter(sys.argv[1] if len(sys.argv) > 1 else ".").run())
