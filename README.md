# PBIX Migrator

PBIX Migrator is a local migration workspace for moving Power BI PBIX report models through Snowflake and Microsoft Fabric Lakehouse, then rebuilding Power BI semantic models and reports. The repository contains a React frontend, .NET migration APIs, and Python/FastAPI support services used for authentication, workspace discovery, PBIX parsing, Snowflake metadata, and migration orchestration.

## Architecture

- `frontend/` - React browser application for migration sessions, Microsoft delegated authentication, workspace selection, report embedding, and Power BI visual workflows.
- `backend/` - backend services and Docker entry point.
- `backend/DOTNET/` - ASP.NET Core migration API used for the current migration flow. It contains Power BI REST, Fabric Lakehouse, Snowflake, XMLA/tabular model, and Azure OpenAI integration services.
- `backend/FASTAPI2/` - FastAPI support service expected by the frontend for workspace/auth related calls on port `8000`.
- `backend/FASTAPI1/` - additional Python migration support service retained by the project.
- `b1/` - Python PBIX/Snowflake migration pipeline support service retained by the project.

## Technologies

- .NET 8 / ASP.NET Core
- Microsoft Power BI REST APIs, XMLA/TOM tabular model APIs, and Fabric APIs
- Microsoft Fabric Lakehouse and SQL analytics endpoint access
- Snowflake
- Azure OpenAI
- Python / FastAPI
- React 18, `react-scripts`, Microsoft Authentication Library, and Power BI JavaScript SDK packages
- Docker Compose for the root backend/frontend container setup

## Prerequisites

- Windows PowerShell
- .NET SDK 8.x
- Node.js and npm
- Python 3.10+ with pip
- Docker Desktop, only if using `docker compose`
- Access to the required Power BI/Fabric workspace, Lakehouse, Snowflake account, and Azure OpenAI deployment

## Local Startup

Start the .NET migration API used by the frontend during local development:

```powershell
cd D:\pbi\SV\backend\DOTNET
dotnet restore
dotnet run
```

The development profile listens on:

```text
http://localhost:5001
```

Start the FastAPI support service expected by the frontend:

```powershell
cd D:\pbi\SV\backend\FASTAPI2
py -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python serve.py
```

The frontend expects this service at:

```text
http://localhost:8000
```

Start the frontend:

```powershell
cd D:\pbi\SV\frontend
npm install
npm start
```

The React development server listens on:

```text
http://localhost:3000
```

Run the Docker Compose setup from the repository root:

```powershell
cd D:\pbi\SV
docker compose up --build
```

Docker Compose exposes:

```text
Frontend: http://localhost:3000
Backend:  http://localhost:5000
```

## Environment Configuration

Do not commit secrets. Keep environment-specific values in local environment variables or the existing `.env` files.

The .NET migration API reads these environment variables:

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

The frontend example configuration is in `frontend/.env.example`:

```text
VITE_MIGRATION_API_URL=http://localhost:5001
VITE_WORKSPACE_API_URL=http://localhost:8000
```

The frontend source defaults to `http://localhost:5001` for the migration API and `http://localhost:8000` for workspace/auth support calls when those variables are not supplied.
