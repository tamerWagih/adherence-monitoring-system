# Adherence Backend Service

NestJS backend service for Agent Adherence Monitoring System.

## Setup

1. Install dependencies:
   ```bash
   npm install
   ```

2. Create `.env` file (or use environment variables from docker-compose):
   ```bash
   cp .env.template .env
   ```

3. Run in development mode:
   ```bash
   npm run start:dev
   ```

4. Build for production:
   ```bash
   npm run build
   npm run start:prod
   ```

## API Endpoints

- Health Check: `GET /health`
- API Base: `GET /api/adherence`

## Database

Connects to shared PostgreSQL database (same as people-ops-system).

## Modules

- **Adherence Module**: Event ingestion, summaries, reporting (to be implemented)
- **Auth Module**: JWT authentication for Admin Frontend (to be implemented)
- **Workstation Module**: Workstation management (to be implemented)

