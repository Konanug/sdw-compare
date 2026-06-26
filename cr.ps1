<#
.SYNOPSIS
    Runs the CodeRabbit CLI (cr) via WSL against this repository.

.DESCRIPTION
    Pass any 'cr' arguments through. Common uses:
      .\cr.ps1                        # Review all uncommitted + committed changes
      .\cr.ps1 -t uncommitted         # Review only staged/unstaged changes
      .\cr.ps1 --base main            # Diff against main branch
      .\cr.ps1 auth login             # Authenticate with CodeRabbit
      .\cr.ps1 --version              # Show installed CLI version

.NOTES
    Requires WSL 2 with a Linux distro and 'cr' installed inside WSL.
    Install CodeRabbit CLI in WSL:
      curl -fsSL https://cli.coderabbit.ai/install.sh | sh
      source ~/.bashrc
      cr auth login
#>
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Args
)

$unixPath = "/mnt/" + ($PSScriptRoot -replace "\\", "/" -replace "^([A-Z]):", { $_.ToLower()[0] })

$crArgs = $Args -join " "
wsl -e bash -ic "cd '$unixPath' && cr $crArgs"
