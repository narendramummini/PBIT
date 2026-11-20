# Backend

The backend folder contains the .NET and Python services used by the PBIX migration pipeline. These services handle migration session APIs, Power BI/Fabric integration, Snowflake metadata and data movement support, PBIX parsing support, authentication support, and semantic model operations.

## Components

- `Controllers/`, `Models/`, `Services/`, `Program.cs`, `PbixMigrator.csproj` - root ASP.NET Core backend used by the Docker backend image on port `5000`.
- `DOTNET/` - ASP.NET Core migration API used for local development on port `5001`. Important service classes include `PowerBIService`, `FabricLakehouseService`, `TabularModelService`, `SnowflakeService`, `AzureOpenAIService`, and `B2MigrationBridge`.
- `FASTAPI2/` - FastAPI support service used by the frontend for workspace and authentication-related calls. The frontend expects it at `http://localhost:8000`.
- `FASTAPI1/` - additional FastAPI migration support service retained by the project.
- `../b1/` - Python PBIX/Snowflake migration pipeline support service retained by the project.

## Dependencies

- .NET SDK 8.x
- Python 3.10+
- pip
- Docker Desktop, only for container startup
- Power BI/Fabric, Snowflake, and Azure OpenAI credentials with the permissions required by the migration flow

## Start The .NET Development API

```powershell
cd D:\pbi\SV\backend\DOTNET
dotnet restore
dotnet run
```

Development URL:

```text
http://localhost:5001
```

The launch profile sets:

```text
ASPNETCORE_ENVIRONMENT=Development
ASPNETCORE_URLS=http://localhost:5001
```

## Start The Docker Backend

```powershell
cd D:\pbi\SV
docker compose up --build backend
```

Docker backend URL:

```text
http://localhost:5000
```

## Start FASTAPI2

```powershell
cd D:\pbi\SV\backend\FASTAPI2
py -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python serve.py
```

Expected local URL:

```text
http://localhost:8000
```

## Start FASTAPI1

```powershell
cd D:\pbi\SV\backend\FASTAPI1
py -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python serve.py
```

## Start The b1 Pipeline Service

```powershell
cd D:\pbi\SV\b1
py -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python serve.py
```

## Required Environment Variables

The `backend/DOTNET` service reads this backend configuration from environment variables and from the existing local `.env` sources when available:

```text
MIGRATION_AUTHORITY
MIGRATION_AUDIENCE
MIGRATION_SIGNING_KEY
AUTH_INTERNAL_SERVICE_KEY
AUTH_SERVICE_URL
FRONTEND_ORIGINS
AZURE_OPENAI_API_KEY
AZURE_OPENAI_ENDPOINT
AZURE_OPENAI_DEPLOYMENT
B2_API_URL
MIGRATION_TICKET_SECRET
```

Production requires valid Azure OpenAI settings, `B2_API_URL`, and a `MIGRATION_TICKET_SECRET` of at least 32 bytes.

The Python services use their existing `.env` files for Snowflake, Microsoft/Fabric/Power BI authentication, and Azure OpenAI settings. Keep secrets out of source control.

## API Ports

```text
.NET development API: http://localhost:5001
Docker backend API:   http://localhost:5000
FastAPI support API:  http://localhost:8000
```
