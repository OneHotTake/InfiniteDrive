#!/bin/bash
export PATH="$PATH:/home/onehottake/.dotnet/tools"
dotnet-sonarscanner begin /k:EmbyPlugin /d:sonar.host.url=https://sonarcloud.io /d:sonar.organization=emby /d:sonar.login=****** /d:sonar.password=******
dotnet build EmbyPlugin.sln
dotnet-sonarscanner end /d:sonar.login=****** /d:sonar.password=******