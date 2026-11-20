# Frontend

The frontend is a React application for running PBIX migration sessions, selecting workspaces, performing Microsoft delegated authentication, displaying migration progress, and embedding Power BI reports.

## Technologies

- React 18
- `react-scripts`
- `@azure/msal-browser`
- `axios`
- `powerbi-client`
- `powerbi-client-react`
- `powerbi-models`
- `powerbi-report-authoring`

## Install Dependencies

```powershell
cd D:\pbi\SV\frontend
npm install
```

## Start Development Server

```powershell
cd D:\pbi\SV\frontend
npm start
```

Frontend URL:

```text
http://localhost:3000
```

## Build

```powershell
cd D:\pbi\SV\frontend
npm run build
```

Build output is written to:

```text
frontend/build
```

## Backend Connection

The checked-in `frontend/.env.example` defines the public service URLs used by the frontend source:

```text
VITE_MIGRATION_API_URL=http://localhost:5001
VITE_WORKSPACE_API_URL=http://localhost:8000
```

The frontend source falls back to:

```text
Migration API: http://localhost:5001
Workspace API: http://localhost:8000
```

The checked-in `frontend/package.json` also contains a development proxy:

```text
http://localhost:5000
```

Use the `backend/DOTNET` service on port `5001` for local migration development, or the Docker backend on port `5000` when running through Docker Compose.
