@echo off
REM Helper for running SQL queries against the local Docker SQL Server.
REM Usage:  sql "SELECT Id, Status FROM Entities"
REM
REM Requires:
REM   - Docker container "cmsevents-sqlserver" running (docker compose up -d).
REM   - Windows CMD or PowerShell. On bash/zsh, use the "sql" shell function from samples/scenarios.md instead.

docker exec cmsevents-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Local_Dev_Password_123!" -C -d CmsEventsDb -Q %*
