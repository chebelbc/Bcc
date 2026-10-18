#!/usr/bin/env python3
"""
Unit tests for the static catalog extractor and the classification rules.

The fixture below is a *synthetic* miniature of the MilestonePSTools repository layout (platyPS docs, PowerShell
function, C# cmdlet). The command names (Get-FixtureItem, Set-FixtureItem, Remove-FixtureItem) are invented for the
test and do not exist in MilestonePSTools.

Run: python3 -m unittest discover -s tools/catalog -p "test_*.py"
"""
import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import classification  # noqa: E402
import extract_repo_catalog as ex  # noqa: E402

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

DOC_GET = """---
external help file: MilestonePSTools-help.xml
online version: https://example.invalid/Get-FixtureItem
schema: 2.0.0
---

# Get-FixtureItem

## SYNOPSIS
Gets fixture items.

## SYNTAX

### ByName (Default)
```
Get-FixtureItem [[-Name] <String>] [<CommonParameters>]
```

### ById
```
Get-FixtureItem -Id <Guid> [<CommonParameters>]
```

## DESCRIPTION
Returns items.

REQUIREMENTS

- Requires VMS connection and will attempt to connect automatically

## EXAMPLES

### Example 1
```powershell
Get-FixtureItem -Name 'x*'
```

Gets items.

## PARAMETERS

### -Id
The item id.

```yaml
Type: Guid
Parameter Sets: ById
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: True (ByPropertyName)
Accept wildcard characters: False
```

### -Name
The item name.

```yaml
Type: String
Parameter Sets: ByName
Aliases: DisplayName

Required: False
Position: 0
Default value: None
Accept pipeline input: False
Accept wildcard characters: True
```

### CommonParameters
This cmdlet supports the common parameters.

## INPUTS

### None

## OUTPUTS

### VideoOS.Platform.ConfigurationItems.Camera

## NOTES

## RELATED LINKS
"""

DOC_SET = """---
online version: https://example.invalid/Set-FixtureItem
---

# Set-FixtureItem

## SYNOPSIS
Sets a fixture item.

## SYNTAX

```
Set-FixtureItem -Item <Camera> [-Mode <String>] [-Password <SecureString>] [-WhatIf] [-Confirm] [<CommonParameters>]
```

## DESCRIPTION
Changes an item.

## PARAMETERS

### -Item
```yaml
Type: Camera
Parameter Sets: (All)
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: True (ByValue)
Accept wildcard characters: False
```

### -Mode
```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Password
```yaml
Type: SecureString
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -WhatIf
```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: wi

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```
"""

PS_SET = """function Set-FixtureItem {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [Alias('Set-FixtureThing')]
    [RequiresVmsConnection()]
    [RequiresVmsVersion('21.1')]
    param(
        [Parameter(Mandatory, ValueFromPipeline)]
        [MipItemTransformation([Camera])]
        [Camera] $Item,

        # A comment with a ) parenthesis and a 'quote
        [ValidateSet('Fast', 'Slow')]
        [string] $Mode,

        [securestring] $Password
    )
    process {
        if ($PSCmdlet.ShouldProcess($Item.Name, 'Set')) { $null = "{ not a brace }" }
    }
}

function Invoke-FixtureHelper {
    param([string] $X)
}
"""

CS_REMOVE = """using System.Management.Automation;
namespace MilestonePSTools.Fixture
{
    public abstract class FixtureBase : PSCmdlet
    {
        [Parameter(Mandatory = true)]
        [ValidateVmsItemType("Camera", "Microphone")]
        public object Target { get; set; }
    }

    [Cmdlet(VerbsCommon.Remove, "Fixture" + nameof(Item), SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
    [RequiresVmsConnection]
    [RequiresVmsConnection()]
    [RequiresElevation()]
    [Alias("Remove-FixtureThing")]
    public class RemoveFixtureItem : FixtureBase
    {
        [Parameter()]
        public SwitchParameter Force { get; set; }
    }
}
"""


class ExtractorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = tempfile.mkdtemp(prefix="pstools-fixture-")
        files = {
            "docs/commands/en-US/Get-FixtureItem.md": DOC_GET,
            "docs/commands/en-US/Set-FixtureItem.md": DOC_SET,
            "docs/commands/en-US/about_Fixture.md": "# about",
            "docs/changelog.md": "# Changelog\n\n## [Unreleased]\n\n## [9.8.7] 2024-05-06\n\n- x\n",
            "MilestonePSTools/Public/Set-FixtureItem.ps1": PS_SET,
            "MilestonePSTools/deprecations.psd1": "@{ 'Get-OldFixture' = 'Get-FixtureItem' }",
            "src/MilestonePSTools/Fixture/RemoveFixtureItem.cs": CS_REMOVE,
            "version.json": json.dumps({"version": "9.9"}),
        }
        for rel, text in files.items():
            path = os.path.join(cls.root, rel)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w", encoding="utf-8") as f:
                f.write(text)
        cls.catalog = ex.build_catalog(cls.root)
        cls.by_name = {c["name"]: c for c in cls.catalog["commands"]}

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.root, ignore_errors=True)

    def test_merges_docs_and_sources(self):
        self.assertEqual(self.catalog["moduleVersion"], "9.8.7")
        self.assertEqual(sorted(self.by_name), ["Get-FixtureItem", "Invoke-FixtureHelper", "Remove-FixtureItem", "Set-FixtureItem"])
        issues = {(d["command"], d["issue"].split()[0]) for d in self.catalog["discrepancies"]}
        self.assertIn(("Get-FixtureItem", "Documented"), issues)
        self.assertIn(("Remove-FixtureItem", "Exported"), issues)
        self.assertEqual(self.catalog["deprecations"], {"Get-OldFixture": "Get-FixtureItem"})

    def test_parses_parameter_sets_and_pipeline_input(self):
        g = self.by_name["Get-FixtureItem"]
        sets = {s["name"]: s for s in g["parameterSets"]}
        self.assertTrue(sets["ByName"]["isDefault"])
        self.assertFalse(sets["ById"]["isDefault"])
        self.assertEqual([p["name"] for p in sets["ById"]["parameters"]], ["Id"])
        idp = next(p for p in g["parameters"] if p["name"] == "Id")
        self.assertTrue(idp["mandatoryInAnySet"])
        self.assertTrue(idp["valueFromPipelineByPropertyName"])
        name = next(p for p in g["parameters"] if p["name"] == "Name")
        self.assertEqual(name["aliases"], ["DisplayName"])
        self.assertTrue(name["acceptsWildcards"])
        self.assertEqual(name["sets"][0]["position"], 0)
        self.assertEqual(g["outputTypes"], ["VideoOS.Platform.ConfigurationItems.Camera"])
        self.assertEqual(g["requirements"]["documented"], ["Requires VMS connection and will attempt to connect automatically"])
        self.assertIn("Get-FixtureItem -Name 'x*'", g["examples"][0]["code"])

    def test_parses_powershell_function_attributes(self):
        s = self.by_name["Set-FixtureItem"]
        self.assertEqual(s["implementation"], "PowerShell")
        self.assertTrue(s["supportsShouldProcess"])
        self.assertEqual(s["confirmImpact"], "Medium")
        self.assertEqual(s["aliases"], ["Set-FixtureThing"])
        self.assertTrue(s["requirements"]["vmsConnection"])
        self.assertEqual(s["requirements"]["minVmsVersion"], "21.1")
        mode = next(p for p in s["parameters"] if p["name"] == "Mode")
        self.assertEqual(mode["acceptedValues"], ["Fast", "Slow"])
        item = next(p for p in s["parameters"] if p["name"] == "Item")
        self.assertEqual(item["mipItemType"], "Camera")
        self.assertTrue(self.by_name["Invoke-FixtureHelper"]["internalHelper"])
        self.assertFalse(s["internalHelper"])

    def test_parses_csharp_cmdlets_with_inherited_parameters(self):
        r = self.by_name["Remove-FixtureItem"]
        self.assertEqual(r["implementation"], "C#")
        self.assertTrue(r["supportsShouldProcess"])
        self.assertEqual(r["confirmImpact"], "High")
        self.assertTrue(r["requirements"]["elevation"])
        self.assertTrue(r["requirements"]["vmsConnection"])
        self.assertEqual(r["aliases"], ["Remove-FixtureThing"])

    def test_bracket_matching_ignores_strings_and_comments(self):
        text = "{ 'a}' \"b}\" # c}\n <# d} #> { } }"
        self.assertEqual(ex.find_matching(text, 0, "{", "}"), len(text) - 1)
        self.assertEqual(len(ex.split_top_level("[int] $a, [ValidateSet('x,y')] $b, # c, d\n $e")), 3)

    def test_csharp_name_expressions(self):
        self.assertEqual(ex.eval_cs_name('"Vms" + nameof(Camera) + "Report"'), "VmsCameraReport")


class ClassificationTests(unittest.TestCase):
    def _classify(self, cmd):
        catalog = {"commands": [cmd]}
        classification.apply_classification(catalog)
        return catalog["commands"][0]

    def _cmd(self, name, **kw):
        verb, _, noun = name.partition("-")
        base = {"name": name, "verb": verb, "noun": noun, "parameters": [], "parameterSets": [], "supportsShouldProcess": False,
                "confirmImpact": None, "requirements": {}, "inputs": [], "outputTypes": [], "examples": [], "deprecated": False,
                "internalHelper": False, "description": "", "synopsis": ""}
        base.update(kw)
        return base

    def test_risk_follows_verbs_overrides_and_confirm_impact(self):
        self.assertEqual(self._classify(self._cmd("Get-VmsCamera"))["risk"]["level"], "ReadOnly")
        self.assertEqual(self._classify(self._cmd("Remove-VmsHardware"))["risk"]["level"], "Delete")
        self.assertEqual(self._classify(self._cmd("Get-VmsHardwarePassword"))["risk"]["level"], "Sensitive")
        high = self._classify(self._cmd("Set-VmsCamera", supportsShouldProcess=True, confirmImpact="High"))
        self.assertEqual(high["risk"]["level"], "HighImpact")

    def test_secret_parameters_are_detected(self):
        self.assertTrue(classification.is_secret_parameter({"name": "Password", "type": "SecureString"}))
        self.assertTrue(classification.is_secret_parameter({"name": "Credential", "type": "PSCredential"}))
        self.assertFalse(classification.is_secret_parameter({"name": "Name", "type": "String"}))

    def test_rules_file_is_the_same_one_the_application_embeds(self):
        with open(os.path.join(REPO, "catalog", "classification-rules.json"), encoding="utf-8") as f:
            rules = json.load(f)
        self.assertIn("verbRisk", rules)
        self.assertEqual(rules["verbRisk"]["Remove"], "Delete")


class CommittedCatalogTests(unittest.TestCase):
    def test_committed_catalog_is_classified_and_consistent(self):
        with open(os.path.join(REPO, "catalog", "milestonepstools-catalog.json"), encoding="utf-8") as f:
            catalog = json.load(f)
        names = [c["name"] for c in catalog["commands"]]
        self.assertEqual(len(names), len(set(names)))
        for c in catalog["commands"]:
            self.assertIn("risk", c)
            self.assertIn(c["guiSuitability"], {"AutoForm", "CustomWorkflow", "ExpertOnly", "AppManaged", "NotSuitable"}, c["name"])
            for p in c["parameters"]:
                if p["type"] in ("SecureString", "PSCredential"):
                    self.assertTrue(p["isSecret"], c["name"] + " -" + p["name"])


class GeneratedDocsTests(unittest.TestCase):
    def test_committed_docs_are_up_to_date(self):
        import report
        with open(os.path.join(REPO, "catalog", "milestonepstools-catalog.json"), encoding="utf-8") as f:
            catalog = json.load(f)
        with open(os.path.join(REPO, "catalog", "gui-support.json"), encoding="utf-8") as f:
            support = json.load(f)
        tmp = tempfile.mkdtemp(prefix="docs-")
        try:
            for name, writer, committed in (
                    ("inv.md", lambda p: report.write_report(catalog, p), os.path.join(REPO, "docs", "COMMAND_INVENTORY.md")),
                    ("sup.md", lambda p: report.write_supported_commands(catalog, support, p), os.path.join(REPO, "SUPPORTED_COMMANDS.md"))):
                out = os.path.join(tmp, name)
                writer(out)
                with open(out, encoding="utf-8") as a, open(committed, encoding="utf-8") as b:
                    self.assertEqual(a.read(), b.read(), committed + " is stale; run tools/catalog/generate_docs.py")
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

if __name__ == "__main__":
    unittest.main()
