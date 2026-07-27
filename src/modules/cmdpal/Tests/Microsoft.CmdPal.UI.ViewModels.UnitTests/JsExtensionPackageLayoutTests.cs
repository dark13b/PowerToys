// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using Microsoft.CmdPal.UI.ViewModels.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.CmdPal.UI.ViewModels.UnitTests;

[TestClass]
public class JsExtensionPackageLayoutTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "cmdpal-layout-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void ResolveRequestedPackage_ResolvesScopedPackage()
    {
        var stagingDir = Path.Combine(_root, "staging");
        var packageDir = CreateCmdPalPackage(stagingDir, "@contoso/sample", "1.2.3", "Contoso Sample");

        // A hoisted runtime dependency that is not a Command Palette extension must not confuse
        // the resolution.
        CreatePlainDependency(stagingDir, "left-pad");

        var resolution = JsExtensionPackageLayout.ResolveRequestedPackage(stagingDir, "@contoso/sample");

        Assert.IsTrue(resolution.Succeeded, resolution.ErrorMessage);
        Assert.AreEqual(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDir)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolution.PackageDirectory!)));
    }

    [TestMethod]
    public void ResolveRequestedPackage_ResolvesUnscopedPackage()
    {
        var stagingDir = Path.Combine(_root, "staging");
        var packageDir = CreateCmdPalPackage(stagingDir, "sample-ext", "1.0.0", "Sample");

        var resolution = JsExtensionPackageLayout.ResolveRequestedPackage(stagingDir, "sample-ext");

        Assert.IsTrue(resolution.Succeeded, resolution.ErrorMessage);
        Assert.AreEqual(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDir)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolution.PackageDirectory!)));
    }

    [TestMethod]
    public void ResolveRequestedPackage_RejectsAmbiguousLayout()
    {
        var stagingDir = Path.Combine(_root, "staging");
        CreateCmdPalPackage(stagingDir, "sample-ext", "1.0.0", "Sample");
        CreateCmdPalPackage(stagingDir, "other-ext", "2.0.0", "Other");

        var resolution = JsExtensionPackageLayout.ResolveRequestedPackage(stagingDir, "sample-ext");

        Assert.IsFalse(resolution.Succeeded);
        Assert.IsNull(resolution.PackageDirectory);
        StringAssert.Contains(resolution.ErrorMessage, "ambiguous");
    }

    [TestMethod]
    public void ResolveRequestedPackage_RejectsAmbiguousScopedAndUnscopedLayout()
    {
        var stagingDir = Path.Combine(_root, "staging");
        CreateCmdPalPackage(stagingDir, "@contoso/sample", "1.0.0", "Scoped");
        CreateCmdPalPackage(stagingDir, "plain-ext", "1.0.0", "Unscoped");

        var resolution = JsExtensionPackageLayout.ResolveRequestedPackage(stagingDir, "@contoso/sample");

        Assert.IsFalse(resolution.Succeeded);
        StringAssert.Contains(resolution.ErrorMessage, "ambiguous");
    }

    [TestMethod]
    public void ResolveRequestedPackage_Fails_WhenRequestedPackageIsNotThePresentOne()
    {
        var stagingDir = Path.Combine(_root, "staging");
        CreateCmdPalPackage(stagingDir, "other-ext", "1.0.0", "Other");

        var resolution = JsExtensionPackageLayout.ResolveRequestedPackage(stagingDir, "sample-ext");

        Assert.IsFalse(resolution.Succeeded);
        Assert.IsNull(resolution.PackageDirectory);
    }

    [TestMethod]
    public void ResolveRequestedPackage_Fails_WhenInstalledPackageIsNotACmdPalExtension()
    {
        var stagingDir = Path.Combine(_root, "staging");
        CreatePlainDependency(stagingDir, "plain-lib");

        var resolution = JsExtensionPackageLayout.ResolveRequestedPackage(stagingDir, "plain-lib");

        Assert.IsFalse(resolution.Succeeded);
    }

    [TestMethod]
    public void ResolveRequestedPackage_Fails_WhenIdentityEscapesNodeModules()
    {
        var stagingDir = Path.Combine(_root, "staging");
        CreateCmdPalPackage(stagingDir, "sample-ext", "1.0.0", "Sample");

        var resolution = JsExtensionPackageLayout.ResolveRequestedPackage(stagingDir, "../../escape");

        Assert.IsFalse(resolution.Succeeded);
    }

    [TestMethod]
    public void AssembleDiscoveryLayout_ProducesDiscoverableTree_WithHoistedDependencies()
    {
        var stagingDir = Path.Combine(_root, "staging");
        var packageDir = CreateCmdPalPackage(stagingDir, "sample-ext", "1.0.0", "Sample");
        CreatePlainDependency(stagingDir, "left-pad");

        // A version-pinned dependency nested inside the package's own node_modules.
        var nestedDep = Path.Combine(packageDir, "node_modules", "nested-dep");
        Directory.CreateDirectory(nestedDep);
        File.WriteAllText(Path.Combine(nestedDep, "index.js"), "module.exports = 1;");

        var resolution = JsExtensionPackageLayout.ResolveRequestedPackage(stagingDir, "sample-ext");
        Assert.IsTrue(resolution.Succeeded, resolution.ErrorMessage);

        var assembled = JsExtensionPackageLayout.AssembleDiscoveryLayout(stagingDir, resolution.PackageDirectory!);

        Assert.IsTrue(File.Exists(Path.Combine(assembled, "package.json")));
        Assert.IsTrue(File.Exists(Path.Combine(assembled, "dist", "extension.js")));
        Assert.IsTrue(Directory.Exists(Path.Combine(assembled, "node_modules", "left-pad")), "The hoisted dependency should be moved under the package node_modules.");
        Assert.IsTrue(Directory.Exists(Path.Combine(assembled, "node_modules", "nested-dep")), "The package's own nested dependency should be preserved.");

        // Promote the assembled tree into a discovery root and confirm the scan finds exactly it.
        var discoveryRoot = Path.Combine(_root, "discovery");
        Directory.CreateDirectory(discoveryRoot);
        var target = Path.Combine(discoveryRoot, "sample-ext");
        Directory.Move(assembled, target);

        var discovered = JsonRpcExtensionService.DiscoverManifests(discoveryRoot);
        Assert.AreEqual(1, discovered.Count);
        Assert.AreEqual("sample-ext", discovered[0].Manifest.Name);
        Assert.AreEqual("Sample", discovered[0].Manifest.DisplayName);
    }

    [TestMethod]
    public void AssembleDiscoveryLayout_PreservesAllScopedAndUnscopedHoistedDependencies()
    {
        var stagingDir = Path.Combine(_root, "staging");
        var packageDir = CreateCmdPalPackage(stagingDir, "sample-ext", "1.0.0", "Sample");

        // The package bundles its own version-pinned scoped dependency under @a. This makes the
        // destination @a scope directory already exist when the hoisted @a packages are merged,
        // which is exactly the layout that previously dropped the hoisted siblings.
        var bundledScoped = Path.Combine(packageDir, "node_modules", "@a", "bundled");
        Directory.CreateDirectory(bundledScoped);
        File.WriteAllText(Path.Combine(bundledScoped, "index.js"), "module.exports = 0;");

        // Mixed hoisted dependencies: two packages share the @a scope, one uses @b, one is plain.
        CreatePlainDependency(stagingDir, "@a/one");
        CreatePlainDependency(stagingDir, "@a/two");
        CreatePlainDependency(stagingDir, "@b/three");
        CreatePlainDependency(stagingDir, "plain-pkg");

        var resolution = JsExtensionPackageLayout.ResolveRequestedPackage(stagingDir, "sample-ext");
        Assert.IsTrue(resolution.Succeeded, resolution.ErrorMessage);

        var assembled = JsExtensionPackageLayout.AssembleDiscoveryLayout(stagingDir, resolution.PackageDirectory!);

        var assembledNodeModules = Path.Combine(assembled, "node_modules");
        Assert.IsTrue(Directory.Exists(Path.Combine(assembledNodeModules, "@a", "one")), "Hoisted @a/one must be preserved.");
        Assert.IsTrue(Directory.Exists(Path.Combine(assembledNodeModules, "@a", "two")), "Hoisted @a/two must be preserved alongside its sibling under the same scope.");
        Assert.IsTrue(Directory.Exists(Path.Combine(assembledNodeModules, "@b", "three")), "Hoisted @b/three must be preserved.");
        Assert.IsTrue(Directory.Exists(Path.Combine(assembledNodeModules, "plain-pkg")), "Hoisted unscoped dependency must be preserved.");
        Assert.IsTrue(Directory.Exists(Path.Combine(assembledNodeModules, "@a", "bundled")), "The package's own bundled scoped dependency must not be lost when merging the hoisted scope.");
    }

    private static string CreateCmdPalPackage(string stagingDir, string packageName, string version, string displayName)
    {
        var relative = packageName.Replace('/', Path.DirectorySeparatorChar);
        var packageDir = Path.Combine(stagingDir, "node_modules", relative);
        Directory.CreateDirectory(Path.Combine(packageDir, "dist"));

        var manifest = $$"""
            {
              "name": "{{packageName}}",
              "version": "{{version}}",
              "main": "dist/extension.js",
              "cmdpal": { "displayName": "{{displayName}}" }
            }
            """;
        File.WriteAllText(Path.Combine(packageDir, "package.json"), manifest);
        File.WriteAllText(Path.Combine(packageDir, "dist", "extension.js"), "// entry");
        return packageDir;
    }

    private static void CreatePlainDependency(string stagingDir, string packageName)
    {
        var depDir = Path.Combine(stagingDir, "node_modules", packageName);
        Directory.CreateDirectory(depDir);
        File.WriteAllText(
            Path.Combine(depDir, "package.json"),
            $$"""{ "name": "{{packageName}}", "version": "1.0.0", "main": "index.js" }""");
        File.WriteAllText(Path.Combine(depDir, "index.js"), "module.exports = {};");
    }
}
