<#
.SYNOPSIS
  Lets a SQL client such as DBeaver connect to the local development database with a user name and password.

.DESCRIPTION
  The application connects with Windows authentication through shared memory and needs none of this. A JDBC client (DBeaver)
  can only use TCP and, with a user name, needs "SQL Server and Windows authentication" (mixed mode). This script:
    1. enables the TCP/IP protocol of the instance and gives it a fixed port (default 14330),
    2. switches the instance to mixed-mode authentication,
    3. restarts the SQL Server service (asks first; the API must not be running),
    4. creates (or updates) a SQL login with READ-ONLY access (db_datareader) to the application database.
  The password is requested in the console, never passed on a command line and never stored anywhere.
  Must run in an elevated (administrator) PowerShell. The application keeps using Windows authentication.

.EXAMPLE
  ./scripts/setup-sql-login.ps1
.EXAMPLE
  ./scripts/setup-sql-login.ps1 -Instance MSSQLSERVER01 -Port 14330 -LoginName dbeaver_reader
#>
param(
  [string]$Instance = 'MSSQLSERVER01',
  [string]$Database = 'MevaCareAIHealthCompanion',
  [string]$LoginName = 'dbeaver_reader',
  [int]$Port = 14330
)

$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Run this script in an elevated PowerShell (Run as administrator).'
}
if ($LoginName -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,63}$') { throw 'LoginName may only contain letters, digits and underscores.' }

$secure = Read-Host "Password for the new SQL login '$LoginName'" -AsSecureString
$again = Read-Host 'Repeat the password' -AsSecureString
$plain = [System.Net.NetworkCredential]::new('', $secure).Password
if ($plain -ne [System.Net.NetworkCredential]::new('', $again).Password) { throw 'The passwords do not match.' }
if ($plain.Length -lt 8) { throw 'Use at least 8 characters (the Windows password policy may require more).' }

# --- 1 and 2: registry settings of the instance ---
$instanceKey = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server' |
  Where-Object { $_.PSChildName -like "MSSQL*.$Instance" } | Select-Object -First 1
if (-not $instanceKey) { throw "SQL Server instance '$Instance' was not found in the registry." }

$server = Join-Path $instanceKey.PSPath 'MSSQLServer'
$tcp = Join-Path $server 'SuperSocketNetLib\Tcp'
Set-ItemProperty -Path $tcp -Name Enabled -Value 1
Set-ItemProperty -Path (Join-Path $tcp 'IPAll') -Name TcpDynamicPorts -Value ''
Set-ItemProperty -Path (Join-Path $tcp 'IPAll') -Name TcpPort -Value "$Port"
Set-ItemProperty -Path $server -Name LoginMode -Value 2
Write-Host "TCP/IP enabled on fixed port $Port; mixed-mode authentication switched on."

# --- 3: restart ---
$service = "MSSQL`$$Instance"
$answer = Read-Host "Restart the service '$service' now? The API must be stopped. [y/N]"
if ($answer -notmatch '^(y|yes|da)$') {
  Write-Host 'Not restarted. The settings apply after you restart the service; the login was NOT created yet. Run the script again afterwards.'
  return
}
Restart-Service -Name $service -Force
(Get-Service $service).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))

# --- 4: the read-only login (Windows authentication over shared memory) ---
$escapedPassword = $plain.Replace("'", "''")
$connection = New-Object System.Data.SqlClient.SqlConnection "Server=lpc:localhost\$Instance;Database=master;Integrated Security=true;Connect Timeout=30"
$connection.Open()
try {
  $sql = @"
IF SUSER_ID(N'$LoginName') IS NULL
  CREATE LOGIN [$LoginName] WITH PASSWORD = N'$escapedPassword', CHECK_POLICY = ON;
ELSE
  ALTER LOGIN [$LoginName] WITH PASSWORD = N'$escapedPassword';
EXEC(N'USE [$Database];
  IF USER_ID(N''$LoginName'') IS NULL CREATE USER [$LoginName] FOR LOGIN [$LoginName];
  ALTER ROLE db_datareader ADD MEMBER [$LoginName];');
"@
  $command = $connection.CreateCommand()
  $command.CommandText = $sql
  [void]$command.ExecuteNonQuery()
}
finally {
  $connection.Dispose()
  $plain = $null
}

Write-Host ''
Write-Host "Done. In DBeaver use:"
Write-Host "  Host: localhost   Port: $Port   Database: $Database"
Write-Host "  Authentication: SQL Server Authentication, user '$LoginName', the password you just typed"
Write-Host "  JDBC: jdbc:sqlserver://localhost:$Port;databaseName=$Database;encrypt=true;trustServerCertificate=true"
Write-Host 'The login can only read. The application is unchanged (Windows authentication).'
