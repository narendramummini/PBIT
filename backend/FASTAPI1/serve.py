#!/usr/bin/env python3
"""
serve.py
Convenience launcher: `python3 serve.py` starts the FastAPI server with
sensible defaults. Equivalent to:
    uvicorn api.main:app --host 0.0.0.0 --port 8000 --reload
"""

import argparse

import uvicorn

if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="Run the PBIX -> Snowflake migration API server")
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8000)
    ap.add_argument("--reload", action="store_true", default=True, help="Auto-reload on code changes (default: on)")
    ap.add_argument("--no-reload", dest="reload", action="store_false")
    args = ap.parse_args()

    print(f"Starting API server on http://{args.host}:{args.port}")
    print(f"Interactive docs available at http://{args.host}:{args.port}/docs")
    uvicorn.run("api.main:app", host=args.host, port=args.port, reload=args.reload)
