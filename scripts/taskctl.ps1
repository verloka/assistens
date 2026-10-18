# Windows PowerShell 5.1+; uses only built-in PowerShell and .NET classes.
# State lives in progress/state.json; tasks.json is immutable during normal execution.
[CmdletBinding()]
param(
  [ValidateSet('next','report','list','start','done','block','unblock','doctor')]
  [string]$Action = 'next',
  [string]$Id = '',
  [string]$Note = '',
  [string]$Evidence = '',
  [string]$Test = ''
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$catalogPath = Join-Path $root 'tasks.json'
$statePath = Join-Path $root 'progress/state.json'
$catalog = Get-Content $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$state = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
$tasks = @($catalog.tasks | Sort-Object sequence)

function StateFor([string]$taskId) {
  $prop = $state.task_states.PSObject.Properties[$taskId]
  if ($null -eq $prop) { throw "Unknown task state $taskId" }
  return $prop.Value
}
function TaskFor([string]$taskId) {
  $found = @($tasks | Where-Object { $_.id -eq $taskId })
  if ($found.Count -ne 1) { throw "Unknown task id $taskId" }
  return $found[0]
}
function IsReady($t) {
  if ((StateFor $t.id).status -ne 'pending') { return $false }
  foreach ($dependency in @($t.depends_on)) {
    if ((StateFor $dependency).status -ne 'done') { return $false }
  }
  return $true
}
function ActiveTasks() {
  return @($tasks | Where-Object { (StateFor $_.id).status -eq 'in_progress' })
}
function NextTask() {
  $active = @(ActiveTasks)
  if ($active.Count -gt 0) { return $active[0] }
  $ready = @($tasks | Where-Object { IsReady $_ })
  if ($ready.Count -gt 0) { return $ready[0] }
  return $null
}
function SaveState() {
  $state.updated_at = [DateTimeOffset]::UtcNow.ToString('o')
  $serialized = ConvertTo-Json -InputObject $state -Depth 40
  $tmp = $statePath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
  [IO.File]::WriteAllText($tmp, $serialized + "`n", (New-Object System.Text.UTF8Encoding($false)))
  # On Windows this replaces state atomically and keeps a backup copy.
  $backup = $statePath + '.bak'
  try {
    if ([IO.File]::Exists($statePath)) {
      [IO.File]::Replace($tmp, $statePath, $backup)
    } else {
      [IO.File]::Move($tmp, $statePath)
    }
  } finally {
    if ([IO.File]::Exists($tmp)) { [IO.File]::Delete($tmp) }
  }
}
function PrintTask($task) {
  if ($null -eq $task) { Write-Output 'No ready/in-progress task. Run doctor and inspect blocked dependencies.'; return }
  $st = StateFor $task.id
  Write-Output ("{0} | {1} | {2} | {3}" -f $task.id, $st.status, $task.phase_name, $task.title)
  Write-Output ('Description: ' + $task.description)
  Write-Output ('Depends on: ' + ((@($task.depends_on) -join ', ') -replace '^$', 'none'))
  Write-Output 'Acceptance:'
  foreach ($criteria in @($task.acceptance)) { Write-Output (' - ' + $criteria) }
  Write-Output 'Verification:'
  foreach ($check in @($task.verification)) { Write-Output (' - ' + $check) }
  if ($task.requires_user_or_environment) { Write-Output 'ATTENTION: requires manual / external environment validation.' }
  if ($st.note) { Write-Output ('Last note: ' + $st.note) }
}
function Doctor() {
  $issues = New-Object System.Collections.Generic.List[string]
  $ids = @{}
  foreach ($t in $tasks) {
    if ($ids.ContainsKey([string]$t.id)) { $issues.Add('Duplicate id: ' + $t.id) }
    $ids[[string]$t.id] = $true
    if ($null -eq $state.task_states.PSObject.Properties[[string]$t.id]) { $issues.Add('Missing state: ' + $t.id) }
  }
  foreach ($prop in $state.task_states.PSObject.Properties) {
    if (-not $ids.ContainsKey([string]$prop.Name)) { $issues.Add('Orphan state: ' + $prop.Name) }
  }
  foreach ($t in $tasks) {
    foreach ($dep in @($t.depends_on)) {
      if (-not $ids.ContainsKey([string]$dep)) { $issues.Add('Missing dependency ' + $dep + ' of ' + $t.id); continue }
      if ((TaskFor $dep).sequence -ge $t.sequence) { $issues.Add('Dependency order violation for ' + $t.id + ' -> ' + $dep) }
    }
    $s = StateFor $t.id
    if (@('pending','in_progress','blocked','done') -notcontains $s.status) { $issues.Add('Invalid status for ' + $t.id) }
    if ($s.status -eq 'done') {
      if (@($s.evidence).Count -eq 0 -or [string]::IsNullOrWhiteSpace($s.completed_at)) { $issues.Add('Completed task without evidence: ' + $t.id) }
      foreach ($dep in @($t.depends_on)) {
        if ((StateFor $dep).status -ne 'done') { $issues.Add('Completed task with incomplete dependency: ' + $t.id + ' -> ' + $dep) }
      }
    }
    if ($s.status -eq 'in_progress' -and [string]::IsNullOrWhiteSpace($s.started_at)) { $issues.Add('In progress but missing started_at: ' + $t.id) }
    if ($s.status -eq 'blocked' -and [string]::IsNullOrWhiteSpace($s.note)) { $issues.Add('Blocked task missing reason: ' + $t.id) }
  }
  if (@(ActiveTasks).Count -gt 1) { $issues.Add('More than one in_progress task (single-agent policy)') }
  if ($issues.Count -gt 0) {
    $issues | ForEach-Object { Write-Output ('ERROR: ' + $_) }
    throw ('Doctor found ' + $issues.Count + ' issue(s)')
  }
  Write-Output ('OK: validated ' + $tasks.Count + ' tasks and their states/dependencies.')
}

switch ($Action) {
  'doctor' { Doctor; break }
  'next' { PrintTask (NextTask); break }
  'list' {
    foreach ($t in $tasks) {
      $s = StateFor $t.id
      Write-Output ("{0} [{1}] {2}" -f $t.id,$s.status,$t.title)
    }
    break
  }
  'report' {
    Write-Output ('PersonalAgent backlog: ' + $tasks.Count + ' tasks')
    foreach ($phase in @($catalog.phases)) {
      $phaseTasks = @($tasks | Where-Object { $_.phase -eq $phase.number })
      $done = @($phaseTasks | Where-Object { (StateFor $_.id).status -eq 'done' }).Count
      $blocked = @($phaseTasks | Where-Object { (StateFor $_.id).status -eq 'blocked' }).Count
      Write-Output ("{0}: {1}/{2} done, {3} blocked - {4}" -f $phase.id,$done,$phaseTasks.Count,$blocked,$phase.name)
    }
    Write-Output 'NEXT:'
    PrintTask (NextTask)
    $blocks = @($tasks | Where-Object { (StateFor $_.id).status -eq 'blocked' })
    if ($blocks.Count -gt 0) {
      Write-Output 'BLOCKERS:'
      foreach ($t in $blocks) { Write-Output ($t.id + ': ' + (StateFor $t.id).note) }
    }
    break
  }
  'start' {
    if ([string]::IsNullOrWhiteSpace($Id)) {
      $target = NextTask
      if ($null -eq $target) { throw 'No task available to start.' }
      $Id = $target.id
    }
    $t = TaskFor $Id; $s = StateFor $Id
    $active = @(ActiveTasks)
    if ($active.Count -gt 0 -and $active[0].id -ne $Id) { throw ('Resume ' + $active[0].id + ' first, or block it with a reason.') }
    if ($s.status -eq 'in_progress') { Write-Output ('RESUME ' + $Id); PrintTask $t; break }
    if (-not (IsReady $t)) { throw ('Task ' + $Id + ' is not ready; dependencies or status block it.') }
    $s.status = 'in_progress'; $s.started_at = [DateTimeOffset]::UtcNow.ToString('o')
    $s.attempts = [int]$s.attempts + 1
    $s.note = if ($Note) { $Note } else { 'Work started' }
    $state.last_task_id = $Id
    SaveState
    Write-Output ('STARTED ' + $Id)
    PrintTask $t
    break
  }
  'done' {
    if ([string]::IsNullOrWhiteSpace($Id)) { throw 'Pass -Id' }
    if ([string]::IsNullOrWhiteSpace($Evidence) -or [string]::IsNullOrWhiteSpace($Test)) {
      throw 'Completion requires both -Evidence (code/commit or observed result) and -Test (verification command/result).'
    }
    $t = TaskFor $Id; $s = StateFor $Id
    if ($s.status -ne 'in_progress') { throw ('Only an in_progress task can be completed: ' + $Id) }
    foreach ($dep in @($t.depends_on)) {
      if ((StateFor $dep).status -ne 'done') { throw ('Dependency ' + $dep + ' not done') }
    }
    $s.status = 'done'; $s.completed_at = [DateTimeOffset]::UtcNow.ToString('o')
    $s.evidence = @($Evidence, ('Verification: ' + $Test))
    $s.note = if ($Note) { $Note } else { 'Acceptance verified' }
    $state.last_task_id = $Id
    SaveState
    Write-Output ('DONE ' + $Id)
    Write-Output 'NEXT:'
    PrintTask (NextTask)
    break
  }
  'block' {
    if ([string]::IsNullOrWhiteSpace($Id) -or [string]::IsNullOrWhiteSpace($Note)) { throw 'Pass -Id and a concrete -Note blocker' }
    $null = TaskFor $Id; $s = StateFor $Id
    if (@('pending','in_progress') -notcontains $s.status) { throw 'Only pending/in_progress task can be blocked' }
    $s.status = 'blocked'; $s.note = $Note
    $state.last_task_id = $Id
    SaveState
    Write-Output ('BLOCKED ' + $Id + ': ' + $Note)
    Write-Output 'NEXT:'; PrintTask (NextTask)
    break
  }
  'unblock' {
    if ([string]::IsNullOrWhiteSpace($Id)) { throw 'Pass -Id' }
    $null = TaskFor $Id; $s = StateFor $Id
    if ($s.status -ne 'blocked') { throw 'Only blocked task can be unblocked' }
    $s.status = 'pending'; $s.note = if ($Note) { $Note } else { 'Blocker resolved; pending recheck' }
    SaveState
    Write-Output ('UNBLOCKED ' + $Id)
    break
  }
}
