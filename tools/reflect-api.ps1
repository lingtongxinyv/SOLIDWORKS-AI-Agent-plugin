# SW2026 Interop 反射签名实测工具（开发辅助，不随产品发布）
# 用法: powershell -File tools\reflect-api.ps1 [-Filter "FeatureExtrusion"] [-Mode props|dim]
param([string]$Filter = "", [string]$Mode = "")

$redist = "D:\Program Files\SOLIDWORKS Corp\SOLIDWORKS (2)\api\redist"
$asmSld = [Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.sldworks.dll")
$asmConst = [Reflection.Assembly]::LoadFrom("$redist\SolidWorks.Interop.swconst.dll")

function Show-Type($typeName, [string]$methodFilter = "*") {
    $t = $asmSld.GetType($typeName)
    if (-not $t) { Write-Output "## TYPE NOT FOUND: $typeName"; return }
    Write-Output "## $($t.FullName)"
    $methods = $t.GetMethods() | Where-Object { $_.Name -like $methodFilter -and -not $_.IsSpecialName }
    foreach ($m in $methods) {
        $params = ($m.GetParameters() | ForEach-Object {
            $pt = $_.ParameterType
            $n = if ($pt.IsByRef) { "ref " + $pt.GetElementType().Name } else { $pt.Name }
            "$n $($_.Name)"
        }) -join ", "
        Write-Output "  $($m.Name)($params) -> $($m.ReturnType.Name)"
    }
}

function Show-Props($typeName, [string]$propFilter = "*") {
    $t = $asmSld.GetType($typeName)
    if (-not $t) { Write-Output "## TYPE NOT FOUND: $typeName"; return }
    Write-Output "## $typeName"
    $t.GetProperties() | Where-Object { $_.Name -like $propFilter } | ForEach-Object {
        $acc = if ($_.CanWrite) { "get/set" } else { "get" }
        Write-Output "  prop $($_.PropertyType.Name) $($_.Name) [$acc]"
    }
}

function Show-Enum($enumName) {
    $t = $asmConst.GetType($enumName)
    if (-not $t) { Write-Output "## ENUM NOT FOUND: $enumName"; return }
    Write-Output "## ENUM $($t.FullName)"
    [Enum]::GetValues($t) | ForEach-Object { Write-Output "  $_ = $([Convert]::ToInt64($_))" }
}

if ($Filter -ne "") {
    $asmSld.GetTypes() | Where-Object { $_.IsPublic -and $_.Name -like "*$Filter*" } | ForEach-Object {
        Write-Output "TYPE $($_.FullName)"
    }
    exit 0
}

if ($Mode -eq "props") {
    Show-Props "SolidWorks.Interop.sldworks.IMassProperty2"
    Show-Props "SolidWorks.Interop.sldworks.IDisplayDimension"
    Show-Props "SolidWorks.Interop.sldworks.IFeature" "*Name*"
    Show-Props "SolidWorks.Interop.sldworks.ISketchManager" "*AddToDB*"
    Show-Props "SolidWorks.Interop.sldworks.IDimension" "Value*"
    exit 0
}

if ($Mode -eq "dim") {
    Show-Type "SolidWorks.Interop.sldworks.IModelDoc2" "*Dimension*"
    Show-Type "SolidWorks.Interop.sldworks.ISketchSegment" "*Select*"
    Show-Type "SolidWorks.Interop.sldworks.IDisplayDimension" "*Dimension*"
    Show-Type "SolidWorks.Interop.sldworks.IModelDoc2" "ClearSelection*"
    Show-Type "SolidWorks.Interop.sldworks.ISketchManager" "InsertSketch*"
    Show-Type "SolidWorks.Interop.sldworks.ISketchManager" "*Exit*"
    exit 0
}

if ($Mode -eq "save") {
    Show-Type "SolidWorks.Interop.sldworks.IModelDocExtension" "SaveAs*"
    Show-Type "SolidWorks.Interop.sldworks.IModelDoc2" "Save*"
    Show-Enum "SolidWorks.Interop.swconst.swSaveAsVersion_e"
    Show-Enum "SolidWorks.Interop.swconst.swSaveAsOptions_e"
    exit 0
}

if ($Mode -eq "idim") {
    Show-Props "SolidWorks.Interop.sldworks.IDimension"
    exit 0
}

if ($Mode -eq "draw") {
    Show-Type "SolidWorks.Interop.sldworks.IDrawingDoc" "*View*"
    Show-Type "SolidWorks.Interop.sldworks.IDrawingDoc" "*Sheet*"
    Show-Type "SolidWorks.Interop.sldworks.IDrawingDoc" "*Annotation*"
    Show-Type "SolidWorks.Interop.sldworks.IDrawingDoc" "*Pdf*"
    Show-Type "SolidWorks.Interop.sldworks.IModelDoc2" "*Annotation*"
    Show-Type "SolidWorks.Interop.sldworks.IModelDoc2" "*CustomInfo*"
    Show-Type "SolidWorks.Interop.sldworks.IModelDocExtension" "SaveAs*"
    Show-Type "SolidWorks.Interop.sldworks.ISheet" "*"
    exit 0
}

if ($Mode -eq "drawprops") {
    Show-Props "SolidWorks.Interop.sldworks.IView"
    Show-Props "SolidWorks.Interop.sldworks.ISheet"
    Show-Enum "SolidWorks.Interop.swconst.swStandardViews_e"
    Show-Enum "SolidWorks.Interop.swconst.swDwgPaperSizes_e"
    Show-Enum "SolidWorks.Interop.swconst.swDwgTemplates_e"
    Show-Enum "SolidWorks.Interop.swconst.swSaveAsVersion_e"
    Show-Enum "SolidWorks.Interop.swconst.swSaveAsOptions_e"
    $t = $asmConst.GetType("SolidWorks.Interop.swconst.swUserPreferenceStringValue_e")
    [Enum]::GetValues($t) | Where-Object { $_ -like "*Drawing*" -or $_ -like "*Template*" } | ForEach-Object {
        Write-Output "  PREFS $_ = $([Convert]::ToInt64($_))"
    }
    exit 0
}

if ($Mode -eq "drawview") {
    Show-Type "SolidWorks.Interop.sldworks.IView" "*"
    exit 0
}

Show-Type "SolidWorks.Interop.sldworks.ISketchManager"
Show-Type "SolidWorks.Interop.sldworks.IModelDoc2" "*Select*"
Show-Type "SolidWorks.Interop.sldworks.IModelDocExtension" "*Select*"
Show-Type "SolidWorks.Interop.sldworks.IModelDocExtension" "*Mass*"
Show-Type "SolidWorks.Interop.sldworks.IModelDocExtension" "*Box*"
Show-Type "SolidWorks.Interop.sldworks.IFeatureManager" "*Extrusion*"
Show-Type "SolidWorks.Interop.sldworks.IFeatureManager" "*Cut*"
Show-Type "SolidWorks.Interop.sldworks.IMassProperty2"
Show-Type "SolidWorks.Interop.sldworks.IBody2" "*Box*"
Show-Type "SolidWorks.Interop.sldworks.IPartDoc" "*Bodies*"
Show-Type "SolidWorks.Interop.sldworks.ISketch" "*Name*"
Show-Enum "SolidWorks.Interop.swconst.swEndConditions_e"
Show-Enum "SolidWorks.Interop.swconst.swSelectType_e"
Show-Enum "SolidWorks.Interop.swconst.swStartConditions_e"
Show-Enum "SolidWorks.Interop.swconst.swBodyType_e"
