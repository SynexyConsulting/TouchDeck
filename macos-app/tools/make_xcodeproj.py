"""Writes TouchDeck.xcodeproj (project.pbxproj, workspace data, shared scheme).

The project uses Xcode 16 folder-synchronized groups (objectVersion 77): every file under
TouchDeck/, TouchDeckCore/ and TouchDeckTests/ belongs to its target automatically, so adding
a .swift file needs no project edit. Re-run only to change targets or build settings:
    python macos-app/tools/make_xcodeproj.py
(project.yml describes the same project for XcodeGen, as a fallback.)
"""
import hashlib
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..")
PROJ = os.path.join(ROOT, "TouchDeck.xcodeproj")

BUNDLE_ID = "com.synexyconsulting.touchdeck"
DEPLOY = "14.0"
APP_VERSION = "0.1.0"
T = "\t"


def oid(name):
    return hashlib.md5(name.encode()).hexdigest()[:24].upper()


I = {k: oid(k) for k in """
project main_group products_group
app_group core_group tests_group app_exceptions
app_product core_product tests_product
app_target core_target tests_target
app_sources app_frameworks app_resources app_embed app_script
core_sources core_frameworks core_headers core_resources
tests_sources tests_frameworks tests_resources
bf_link_app bf_embed_core bf_link_tests
proxy_app_core proxy_tests_core dep_app_core dep_tests_core
proj_debug proj_release proj_configs
app_debug app_release app_configs
core_debug core_release core_configs
tests_debug tests_release tests_configs
""".split()}


def q(v):
    s = str(v)
    if s and all(c.isalnum() or c in "._/" for c in s):
        return s
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def settings(d, indent=T * 4):
    out = []
    for k in sorted(d):
        v = d[k]
        if isinstance(v, list):
            items = "".join(f"{indent}{T}{q(x)},\n" for x in v)
            out.append(f"{indent}{k} = (\n{items}{indent});")
        else:
            out.append(f"{indent}{k} = {q(v)};")
    return "\n".join(out)


PROJECT_COMMON = {
    "ALWAYS_SEARCH_USER_PATHS": "NO",
    "CLANG_ENABLE_MODULES": "YES",
    "CLANG_ENABLE_OBJC_ARC": "YES",
    "COPY_PHASE_STRIP": "NO",
    "ENABLE_STRICT_OBJC_MSGSEND": "YES",
    "ENABLE_USER_SCRIPT_SANDBOXING": "NO",
    "GCC_C_LANGUAGE_STANDARD": "gnu17",
    "MACOSX_DEPLOYMENT_TARGET": DEPLOY,
    "SDKROOT": "macosx",
    "SWIFT_VERSION": "5.0",
}
PROJECT_DEBUG = dict(PROJECT_COMMON, **{
    "DEBUG_INFORMATION_FORMAT": "dwarf",
    "ENABLE_TESTABILITY": "YES",
    "GCC_OPTIMIZATION_LEVEL": "0",
    "ONLY_ACTIVE_ARCH": "YES",
    "SWIFT_ACTIVE_COMPILATION_CONDITIONS": "DEBUG $(inherited)",
    "SWIFT_OPTIMIZATION_LEVEL": "-Onone",
})
PROJECT_RELEASE = dict(PROJECT_COMMON, **{
    "DEBUG_INFORMATION_FORMAT": "dwarf-with-dsym",
    "SWIFT_COMPILATION_MODE": "wholemodule",
})

# Signing: "-" (Sign to Run Locally) builds out of the box. To ship, set DEVELOPMENT_TEAM and
# switch Release to "Developer ID Application" (see macos-app/README.md, "Signing").
SIGN = {"CODE_SIGN_STYLE": "Automatic", "CODE_SIGN_IDENTITY": "-", "DEVELOPMENT_TEAM": ""}

APP = dict(SIGN, **{
    "ASSETCATALOG_COMPILER_APPICON_NAME": "AppIcon",
    "ASSETCATALOG_COMPILER_GLOBAL_ACCENT_COLOR_NAME": "AccentColor",
    "CODE_SIGN_ENTITLEMENTS": "TouchDeck/TouchDeck.entitlements",
    "COMBINE_HIDPI_IMAGES": "YES",
    "CURRENT_PROJECT_VERSION": "1",
    "INFOPLIST_FILE": "TouchDeck/Info.plist",
    "LD_RUNPATH_SEARCH_PATHS": ["$(inherited)", "@executable_path/../Frameworks"],
    "MARKETING_VERSION": APP_VERSION,
    "PRODUCT_BUNDLE_IDENTIFIER": BUNDLE_ID,
    "PRODUCT_MODULE_NAME": "TouchDeck",
    "PRODUCT_NAME": "Touch Deck",
})
CORE = dict(SIGN, **{
    "CURRENT_PROJECT_VERSION": "1",
    "DEFINES_MODULE": "YES",
    "DYLIB_COMPATIBILITY_VERSION": "1",
    "DYLIB_CURRENT_VERSION": "1",
    "DYLIB_INSTALL_NAME_BASE": "@rpath",
    "GENERATE_INFOPLIST_FILE": "YES",
    "INSTALL_PATH": "$(LOCAL_LIBRARY_DIR)/Frameworks",
    "LD_RUNPATH_SEARCH_PATHS": ["$(inherited)", "@executable_path/../Frameworks", "@loader_path/Frameworks"],
    "MARKETING_VERSION": APP_VERSION,
    "PRODUCT_BUNDLE_IDENTIFIER": BUNDLE_ID + ".core",
    "PRODUCT_NAME": "TouchDeckCore",
    "SKIP_INSTALL": "YES",
    "VERSIONING_SYSTEM": "apple-generic",
})
TESTS = dict(SIGN, **{
    "CURRENT_PROJECT_VERSION": "1",
    "GENERATE_INFOPLIST_FILE": "YES",
    "MARKETING_VERSION": APP_VERSION,
    "PRODUCT_BUNDLE_IDENTIFIER": BUNDLE_ID + ".tests",
    "PRODUCT_NAME": "TouchDeckTests",
})


def config(key, name, s):
    return (f"{T*2}{I[key]} /* {name} */ = {{\n{T*3}isa = XCBuildConfiguration;\n{T*3}buildSettings = {{\n"
            f"{settings(s)}\n{T*3}}};\n{T*3}name = {name};\n{T*2}}};\n")


def config_list(key, debug, release, label):
    return (f"{T*2}{I[key]} /* Build configuration list for {label} */ = {{\n{T*3}isa = XCConfigurationList;\n"
            f"{T*3}buildConfigurations = (\n{T*4}{I[debug]} /* Debug */,\n{T*4}{I[release]} /* Release */,\n{T*3});\n"
            f"{T*3}defaultConfigurationIsVisible = 0;\n{T*3}defaultConfigurationName = Release;\n{T*2}}};\n")


def phase(key, isa, name, files=(), extra=()):
    fl = "".join(f"{T*4}{I[f]},\n" for f in files)
    ex = "".join(f"{T*3}{line}\n" for line in extra)
    return (f"{T*2}{I[key]} /* {name} */ = {{\n{T*3}isa = {isa};\n{T*3}buildActionMask = 2147483647;\n"
            f"{ex}{T*3}files = (\n{fl}{T*3});\n{T*3}runOnlyForDeploymentPostprocessing = 0;\n{T*2}}};\n")


def target(key, name, configs, phases, deps, group, product, ptype):
    ph = "".join(f"{T*4}{I[p]},\n" for p in phases)
    dp = "".join(f"{T*4}{I[d]},\n" for d in deps)
    return (f"{T*2}{I[key]} /* {name} */ = {{\n{T*3}isa = PBXNativeTarget;\n{T*3}buildConfigurationList = {I[configs]};\n"
            f"{T*3}buildPhases = (\n{ph}{T*3});\n{T*3}buildRules = (\n{T*3});\n{T*3}dependencies = (\n{dp}{T*3});\n"
            f"{T*3}fileSystemSynchronizedGroups = (\n{T*4}{I[group]},\n{T*3});\n{T*3}name = {name};\n"
            f"{T*3}productName = {name};\n{T*3}productReference = {I[product]};\n{T*3}productType = \"{ptype}\";\n{T*2}}};\n")


def proxy(key):
    return (f"{T*2}{I[key]} /* PBXContainerItemProxy */ = {{\n{T*3}isa = PBXContainerItemProxy;\n"
            f"{T*3}containerPortal = {I['project']} /* Project object */;\n{T*3}proxyType = 1;\n"
            f"{T*3}remoteGlobalIDString = {I['core_target']};\n{T*3}remoteInfo = TouchDeckCore;\n{T*2}}};\n")


def dependency(key, proxy_key):
    return (f"{T*2}{I[key]} /* PBXTargetDependency */ = {{\n{T*3}isa = PBXTargetDependency;\n"
            f"{T*3}target = {I['core_target']} /* TouchDeckCore */;\n{T*3}targetProxy = {I[proxy_key]} /* PBXContainerItemProxy */;\n{T*2}}};\n")


def sync_group(key, path, exceptions=None):
    ex = f"exceptions = ({I[exceptions]}, ); " if exceptions else ""
    return (f"{T*2}{I[key]} /* {path} */ = {{isa = PBXFileSystemSynchronizedRootGroup; {ex}"
            f"explicitFileTypes = {{}}; explicitFolders = (); path = {path}; sourceTree = \"<group>\"; }};\n")


SCRIPT = 'sh \\"$SRCROOT/scripts/bundle-native.sh\\"\\n'
CORE_FW = f"{I['core_product']} /* TouchDeckCore.framework */"

objects = "".join([
    "\n/* Begin PBXBuildFile section */\n",
    f"{T*2}{I['bf_link_app']} /* TouchDeckCore.framework in Frameworks */ = {{isa = PBXBuildFile; fileRef = {CORE_FW}; }};\n",
    f"{T*2}{I['bf_embed_core']} /* TouchDeckCore.framework in Embed Frameworks */ = {{isa = PBXBuildFile; fileRef = {CORE_FW}; "
    "settings = {ATTRIBUTES = (CodeSignOnCopy, RemoveHeadersOnCopy, ); }; };\n",
    f"{T*2}{I['bf_link_tests']} /* TouchDeckCore.framework in Frameworks */ = {{isa = PBXBuildFile; fileRef = {CORE_FW}; }};\n",
    "/* End PBXBuildFile section */\n",

    "\n/* Begin PBXContainerItemProxy section */\n", proxy("proxy_app_core"), proxy("proxy_tests_core"),
    "/* End PBXContainerItemProxy section */\n",

    "\n/* Begin PBXCopyFilesBuildPhase section */\n",
    phase("app_embed", "PBXCopyFilesBuildPhase", "Embed Frameworks", ["bf_embed_core"],
          ['dstPath = "";', "dstSubfolderSpec = 10;", 'name = "Embed Frameworks";']),
    "/* End PBXCopyFilesBuildPhase section */\n",

    "\n/* Begin PBXFileReference section */\n",
    f"{T*2}{I['app_product']} /* Touch Deck.app */ = {{isa = PBXFileReference; explicitFileType = wrapper.application; "
    "includeInIndex = 0; path = \"Touch Deck.app\"; sourceTree = BUILT_PRODUCTS_DIR; };\n",
    f"{T*2}{I['core_product']} /* TouchDeckCore.framework */ = {{isa = PBXFileReference; explicitFileType = wrapper.framework; "
    "includeInIndex = 0; path = TouchDeckCore.framework; sourceTree = BUILT_PRODUCTS_DIR; };\n",
    f"{T*2}{I['tests_product']} /* TouchDeckTests.xctest */ = {{isa = PBXFileReference; explicitFileType = wrapper.cfbundle; "
    "includeInIndex = 0; path = TouchDeckTests.xctest; sourceTree = BUILT_PRODUCTS_DIR; };\n",
    "/* End PBXFileReference section */\n",

    "\n/* Begin PBXFileSystemSynchronizedBuildFileExceptionSet section */\n",
    f"{T*2}{I['app_exceptions']} /* Exceptions for \"TouchDeck\" folder in \"TouchDeck\" target */ = {{\n"
    f"{T*3}isa = PBXFileSystemSynchronizedBuildFileExceptionSet;\n{T*3}membershipExceptions = (\n{T*4}Info.plist,\n{T*3});\n"
    f"{T*3}target = {I['app_target']} /* TouchDeck */;\n{T*2}}};\n",
    "/* End PBXFileSystemSynchronizedBuildFileExceptionSet section */\n",

    "\n/* Begin PBXFileSystemSynchronizedRootGroup section */\n",
    sync_group("app_group", "TouchDeck", "app_exceptions"), sync_group("core_group", "TouchDeckCore"),
    sync_group("tests_group", "TouchDeckTests"),
    "/* End PBXFileSystemSynchronizedRootGroup section */\n",

    "\n/* Begin PBXFrameworksBuildPhase section */\n",
    phase("app_frameworks", "PBXFrameworksBuildPhase", "Frameworks", ["bf_link_app"]),
    phase("core_frameworks", "PBXFrameworksBuildPhase", "Frameworks"),
    phase("tests_frameworks", "PBXFrameworksBuildPhase", "Frameworks", ["bf_link_tests"]),
    "/* End PBXFrameworksBuildPhase section */\n",

    "\n/* Begin PBXGroup section */\n",
    f"{T*2}{I['main_group']} = {{\n{T*3}isa = PBXGroup;\n{T*3}children = (\n"
    f"{T*4}{I['app_group']} /* TouchDeck */,\n{T*4}{I['core_group']} /* TouchDeckCore */,\n"
    f"{T*4}{I['tests_group']} /* TouchDeckTests */,\n{T*4}{I['products_group']} /* Products */,\n"
    f"{T*3});\n{T*3}sourceTree = \"<group>\";\n{T*2}}};\n",
    f"{T*2}{I['products_group']} /* Products */ = {{\n{T*3}isa = PBXGroup;\n{T*3}children = (\n"
    f"{T*4}{I['app_product']} /* Touch Deck.app */,\n{T*4}{CORE_FW},\n{T*4}{I['tests_product']} /* TouchDeckTests.xctest */,\n"
    f"{T*3});\n{T*3}name = Products;\n{T*3}sourceTree = \"<group>\";\n{T*2}}};\n",
    "/* End PBXGroup section */\n",

    "\n/* Begin PBXHeadersBuildPhase section */\n",
    phase("core_headers", "PBXHeadersBuildPhase", "Headers"),
    "/* End PBXHeadersBuildPhase section */\n",

    "\n/* Begin PBXNativeTarget section */\n",
    target("app_target", "TouchDeck", "app_configs", ["app_sources", "app_frameworks", "app_resources", "app_embed", "app_script"],
           ["dep_app_core"], "app_group", "app_product", "com.apple.product-type.application"),
    target("core_target", "TouchDeckCore", "core_configs", ["core_headers", "core_sources", "core_frameworks", "core_resources"],
           [], "core_group", "core_product", "com.apple.product-type.framework"),
    target("tests_target", "TouchDeckTests", "tests_configs", ["tests_sources", "tests_frameworks", "tests_resources"],
           ["dep_tests_core"], "tests_group", "tests_product", "com.apple.product-type.bundle.unit-test"),
    "/* End PBXNativeTarget section */\n",

    "\n/* Begin PBXProject section */\n",
    f"{T*2}{I['project']} /* Project object */ = {{\n{T*3}isa = PBXProject;\n{T*3}attributes = {{\n"
    f"{T*4}BuildIndependentTargetsInParallel = 1;\n{T*4}LastSwiftUpdateCheck = 1600;\n{T*4}LastUpgradeCheck = 1600;\n"
    f"{T*4}TargetAttributes = {{\n"
    + "".join(f"{T*5}{I[t]} = {{\n{T*6}CreatedOnToolsVersion = 16.0;\n{T*5}}};\n" for t in ("app_target", "core_target", "tests_target"))
    + f"{T*4}}};\n{T*3}}};\n{T*3}buildConfigurationList = {I['proj_configs']};\n{T*3}developmentRegion = en;\n"
    f"{T*3}hasScannedForEncodings = 0;\n{T*3}knownRegions = (\n{T*4}en,\n{T*4}Base,\n{T*3});\n"
    f"{T*3}mainGroup = {I['main_group']};\n{T*3}minimizedProjectReferenceProxies = 1;\n{T*3}preferredProjectObjectVersion = 77;\n"
    f"{T*3}productRefGroup = {I['products_group']} /* Products */;\n{T*3}projectDirPath = \"\";\n{T*3}projectRoot = \"\";\n"
    f"{T*3}targets = (\n{T*4}{I['app_target']} /* TouchDeck */,\n{T*4}{I['core_target']} /* TouchDeckCore */,\n"
    f"{T*4}{I['tests_target']} /* TouchDeckTests */,\n{T*3});\n{T*2}}};\n",
    "/* End PBXProject section */\n",

    "\n/* Begin PBXResourcesBuildPhase section */\n",
    phase("app_resources", "PBXResourcesBuildPhase", "Resources"),
    phase("core_resources", "PBXResourcesBuildPhase", "Resources"),
    phase("tests_resources", "PBXResourcesBuildPhase", "Resources"),
    "/* End PBXResourcesBuildPhase section */\n",

    "\n/* Begin PBXShellScriptBuildPhase section */\n",
    f"{T*2}{I['app_script']} /* Bundle device renderers and firmware */ = {{\n{T*3}isa = PBXShellScriptBuildPhase;\n"
    f"{T*3}alwaysOutOfDate = 1;\n{T*3}buildActionMask = 2147483647;\n{T*3}files = (\n{T*3});\n{T*3}inputFileListPaths = (\n{T*3});\n"
    f"{T*3}inputPaths = (\n{T*3});\n{T*3}name = \"Bundle device renderers and firmware\";\n{T*3}outputFileListPaths = (\n{T*3});\n"
    f"{T*3}outputPaths = (\n{T*3});\n{T*3}runOnlyForDeploymentPostprocessing = 0;\n{T*3}shellPath = /bin/sh;\n"
    f"{T*3}shellScript = \"{SCRIPT}\";\n{T*2}}};\n",
    "/* End PBXShellScriptBuildPhase section */\n",

    "\n/* Begin PBXSourcesBuildPhase section */\n",
    phase("app_sources", "PBXSourcesBuildPhase", "Sources"),
    phase("core_sources", "PBXSourcesBuildPhase", "Sources"),
    phase("tests_sources", "PBXSourcesBuildPhase", "Sources"),
    "/* End PBXSourcesBuildPhase section */\n",

    "\n/* Begin PBXTargetDependency section */\n",
    dependency("dep_app_core", "proxy_app_core"), dependency("dep_tests_core", "proxy_tests_core"),
    "/* End PBXTargetDependency section */\n",

    "\n/* Begin XCBuildConfiguration section */\n",
    config("proj_debug", "Debug", PROJECT_DEBUG), config("proj_release", "Release", PROJECT_RELEASE),
    config("app_debug", "Debug", dict(APP, ENABLE_HARDENED_RUNTIME="NO")),
    config("app_release", "Release", dict(APP, ENABLE_HARDENED_RUNTIME="YES")),
    config("core_debug", "Debug", CORE), config("core_release", "Release", CORE),
    config("tests_debug", "Debug", TESTS), config("tests_release", "Release", TESTS),
    "/* End XCBuildConfiguration section */\n",

    "\n/* Begin XCConfigurationList section */\n",
    config_list("proj_configs", "proj_debug", "proj_release", 'PBXProject "TouchDeck"'),
    config_list("app_configs", "app_debug", "app_release", 'PBXNativeTarget "TouchDeck"'),
    config_list("core_configs", "core_debug", "core_release", 'PBXNativeTarget "TouchDeckCore"'),
    config_list("tests_configs", "tests_debug", "tests_release", 'PBXNativeTarget "TouchDeckTests"'),
    "/* End XCConfigurationList section */\n",
])

PBX = ("// !$*UTF8*$!\n{\n" + f"{T}archiveVersion = 1;\n{T}classes = {{\n{T}}};\n{T}objectVersion = 77;\n{T}objects = {{\n"
       + objects + f"{T}}};\n{T}rootObject = {I['project']} /* Project object */;\n}}\n")


def ref(target, name):
    return (f'<BuildableReference BuildableIdentifier = "primary" BlueprintIdentifier = "{I[target]}" '
            f'BuildableName = "{name}" BlueprintName = "{ {"app_target": "TouchDeck", "tests_target": "TouchDeckTests"}[target] }" '
            f'ReferencedContainer = "container:TouchDeck.xcodeproj"></BuildableReference>')


APP_REF = ref("app_target", "Touch Deck.app")
TESTS_REF = ref("tests_target", "TouchDeckTests.xctest")

SCHEME = f"""<?xml version="1.0" encoding="UTF-8"?>
<Scheme LastUpgradeVersion = "1600" version = "1.7">
   <BuildAction parallelizeBuildables = "YES" buildImplicitDependencies = "YES">
      <BuildActionEntries>
         <BuildActionEntry buildForTesting = "YES" buildForRunning = "YES" buildForProfiling = "YES" buildForArchiving = "YES" buildForAnalyzing = "YES">
            {APP_REF}
         </BuildActionEntry>
      </BuildActionEntries>
   </BuildAction>
   <TestAction buildConfiguration = "Debug" selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB" shouldUseLaunchSchemeArgsEnv = "NO">
      <PreActions>
         <ExecutionAction ActionType = "Xcode.IDEStandardExecutionActionsCore.ExecutionActionType.ShellScriptAction">
            <ActionContent title = "Build the device renderers for the mirror tests" scriptText = "sh &quot;$SRCROOT/../hostui/build.sh&quot; &quot;$SRCROOT/../hostui/out&quot;&#10;">
               <EnvironmentBuildable>
                  {TESTS_REF}
               </EnvironmentBuildable>
            </ActionContent>
         </ExecutionAction>
      </PreActions>
      <EnvironmentVariables>
         <EnvironmentVariable key = "TOUCHDECK_TDUI_DIR" value = "$(SRCROOT)/../hostui/out" isEnabled = "YES"></EnvironmentVariable>
      </EnvironmentVariables>
      <Testables>
         <TestableReference skipped = "NO">
            {TESTS_REF}
         </TestableReference>
      </Testables>
   </TestAction>
   <LaunchAction buildConfiguration = "Debug" selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB" launchStyle = "0" useCustomWorkingDirectory = "NO" ignoresPersistentStateOnLaunch = "NO" debugDocumentVersioning = "YES" debugServiceExtension = "internal" allowLocationSimulation = "YES">
      <BuildableProductRunnable runnableDebuggingMode = "0">
         {APP_REF}
      </BuildableProductRunnable>
   </LaunchAction>
   <ProfileAction buildConfiguration = "Release" shouldUseLaunchSchemeArgsEnv = "YES" savedToolIdentifier = "" useCustomWorkingDirectory = "NO" debugDocumentVersioning = "YES">
      <BuildableProductRunnable runnableDebuggingMode = "0">
         {APP_REF}
      </BuildableProductRunnable>
   </ProfileAction>
   <AnalyzeAction buildConfiguration = "Debug"></AnalyzeAction>
   <ArchiveAction buildConfiguration = "Release" revealArchiveInOrganizer = "YES"></ArchiveAction>
</Scheme>
"""

WORKSPACE = """<?xml version="1.0" encoding="UTF-8"?>
<Workspace
   version = "1.0">
   <FileRef
      location = "self:">
   </FileRef>
</Workspace>
"""


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote", os.path.relpath(path, ROOT))


if __name__ == "__main__":
    write(os.path.join(PROJ, "project.pbxproj"), PBX)
    write(os.path.join(PROJ, "project.xcworkspace", "contents.xcworkspacedata"), WORKSPACE)
    write(os.path.join(PROJ, "xcshareddata", "xcschemes", "TouchDeck.xcscheme"), SCHEME)
