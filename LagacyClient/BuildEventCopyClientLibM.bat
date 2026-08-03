@echo off

set TargetDir="%1"
set ProjectDir="%2"

echo %ProjectDir%

if %ProjectDir%=="C:\_mine\GitHubProject\EcsClientLibM\" (
    xcopy %TargetDir%"*.*" "C:\_mine\GitHubProject\ClaTang\Assets\Plugins\ServerLibM\" /Y        
) else if %ProjectDir%=="C:\_mine\EcsClientLibM\" (
    xcopy %TargetDir%"*.*" "C:\_mine\ClaTang\Assets\Plugins\ServerLibM\" /Y   
) else if %ProjectDir%=="C:\_myPrivate\EcsClientLibM\" (
    xcopy %TargetDir%"*.*" "C:\_myPrivate\ClaTang\Assets\Plugins\ServerLibM\" /Y   
) else if %ProjectDir%=="C:\MyServer\ClientM\" (
    xcopy %TargetDir%"*.*" "C:\MyServer\ClaTang\Assets\Plugins\ServerLibM\" /Y   
    xcopy %TargetDir%"log4netCla.config" "C:\MyServer\ClaTang\Assets\StreamingAssets\" /Y
)

