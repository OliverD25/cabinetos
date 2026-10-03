# A script that does nothing but wait, to prove the laptop's lock and the exact-commit checkout through
# remote-script.ps1 without opening a window: it prints its label and the commit it runs, waits -Seconds, prints its
# label again and exits with -Exit. Two of them started together must run one after the other, each wrapper returning
# its own label and exit code (docs/ui.md, "The live check on another machine").
#
#   remote-script.ps1 -Script ui\livecheck\laptop-probe.ps1 -Args "-Label first -Seconds 90"
param([string]$Label = 'probe', [int]$Seconds = 90, [int]$Exit = 0)
"probe $Label started $(Get-Date -Format 'HH:mm:ss'), commit $(& git rev-parse HEAD)"
Start-Sleep -Seconds $Seconds
"probe $Label ended $(Get-Date -Format 'HH:mm:ss')"
exit $Exit
