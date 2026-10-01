# The warning before a script takes the real keyboard and mouse (CLAUDE.md, "Working rules"): a small
# always-on-top window counts down, with a sound, so whoever sits at the PC knows when to let go of both.
# Esc or the button cancels the run. Dot-source the file and call Show-InputCountdown; it returns $true when
# the count ran out and $false when the person cancelled. Run the file on its own to see the window:
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\countdown.ps1 -Seconds 3
# The runner (run-livecheck.ps1) and the Claude Code probe (claude-terminal.ps1) show it before their first
# key. Runs in Windows PowerShell 5.1 and PowerShell 7.
param([int]$Seconds = 5, [string]$What = 'The live check')

function Show-InputCountdown {
  param([int]$Seconds = 5, [string]$What = 'The live check')
  Add-Type -AssemblyName System.Windows.Forms
  Add-Type -AssemblyName System.Drawing
  $form = New-Object System.Windows.Forms.Form
  $form.Text = 'CabinetOS: the keyboard and mouse are about to be taken'
  $form.TopMost = $true
  $form.StartPosition = 'CenterScreen'
  $form.FormBorderStyle = 'FixedDialog'
  $form.MaximizeBox = $false
  $form.MinimizeBox = $false
  $form.ClientSize = New-Object System.Drawing.Size(560, 215)
  $form.BackColor = [System.Drawing.Color]::FromArgb(255, 204, 64)

  $title = New-Object System.Windows.Forms.Label
  $title.Location = New-Object System.Drawing.Point(10, 12)
  $title.Size = New-Object System.Drawing.Size(540, 36)
  $title.TextAlign = 'MiddleCenter'
  $title.Font = New-Object System.Drawing.Font('Segoe UI', 14)
  $title.Text = "$What takes the keyboard and mouse in"

  $count = New-Object System.Windows.Forms.Label
  $count.Location = New-Object System.Drawing.Point(10, 48)
  $count.Size = New-Object System.Drawing.Size(540, 84)
  $count.TextAlign = 'MiddleCenter'
  $count.Font = New-Object System.Drawing.Font('Segoe UI', 52, [System.Drawing.FontStyle]::Bold)
  $count.Text = "$Seconds"

  $hint = New-Object System.Windows.Forms.Label
  $hint.Location = New-Object System.Drawing.Point(10, 134)
  $hint.Size = New-Object System.Drawing.Size(540, 36)
  $hint.TextAlign = 'MiddleCenter'
  $hint.Font = New-Object System.Drawing.Font('Segoe UI', 10)
  $hint.Text = "Let go of both until a DONE.md opens in Notepad. Esc or the button cancels the run."

  $cancel = New-Object System.Windows.Forms.Button
  $cancel.Location = New-Object System.Drawing.Point(215, 176)
  $cancel.Size = New-Object System.Drawing.Size(130, 30)
  $cancel.Text = 'Cancel the run'
  $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
  $form.CancelButton = $cancel
  $form.Controls.AddRange(@($title, $count, $hint, $cancel))

  $state = @{ left = $Seconds }
  $timer = New-Object System.Windows.Forms.Timer
  $timer.Interval = 1000
  $timer.Add_Tick({
    $state.left--
    if ($state.left -le 0) {
      $timer.Stop()
      $form.DialogResult = [System.Windows.Forms.DialogResult]::OK
      $form.Close()
    } else {
      $count.Text = "$($state.left)"
    }
  }.GetNewClosure())
  $form.Add_Shown({
    [System.Media.SystemSounds]::Exclamation.Play()
    $form.Activate()
    $timer.Start()
  }.GetNewClosure())
  $result = $form.ShowDialog()
  $timer.Dispose()
  $form.Dispose()
  return ($result -eq [System.Windows.Forms.DialogResult]::OK)
}

if ($MyInvocation.InvocationName -ne '.') {
  $ok = Show-InputCountdown -Seconds $Seconds -What $What
  "countdown: $(if ($ok) { 'ran out, the keys would start now' } else { 'cancelled' })"
}
