# Adherence Monitoring System

Agent Adherence Monitoring System - Backend and Admin Frontend

## Architecture

- **Backend**: NestJS service (Adherence Backend)
- **Frontend**: Next.js Admin Frontend
- **Database**: Shared PostgreSQL (same as people-ops-system)
- **Redis**: Shared Redis (for BullMQ queue)
- **Reverse Proxy**: NGINX

## Repository Structure

```
adherence-monitoring-system/
├── backend/          # NestJS Adherence Backend Service
├── frontend/         # Next.js Admin Frontend
├── nginx/            # NGINX configuration
├── docker-compose.yml
├── Dockerfile.backend
├── Dockerfile.frontend
└── .env.template
```

## Quick Start

### Prerequisites

- Docker and Docker Compose
- Access to shared PostgreSQL database (VM 2)
- Access to shared Redis (VM 2)

### Setup

1. Copy environment template:
   ```bash
   cp .env.template .env
   ```

2. Update `.env` with your database and Redis connection details

3. Build and start services:
   ```bash
   docker-compose up -d --build
   ```

4. Access:
   - Frontend: http://localhost (or configured domain)
   - Backend API: http://localhost/api/adherence/...

## Development

### Backend Development

```bash
cd backend
npm install
npm run start:dev
```

### Frontend Development

```bash
cd frontend
npm install
npm run dev
```

## Deployment

### Staging/Production

1. Update `.env` with production database credentials
2. Update NGINX configuration for production domain
3. Build and deploy:
   ```bash
   docker-compose -f docker-compose.yml up -d --build
   ```

## Services

- **Backend**: Port 4001 (internal), exposed via NGINX
- **Frontend**: Port 3001 (internal), exposed via NGINX
- **NGINX**: Port 80 (or configured port)

## Database

Connects to shared PostgreSQL database:
- Tables: `agent_adherence_events`, `agent_adherence_summaries`, `agent_adherence_exceptions`, etc.
- Migrations: Managed in `people-ops-system/database/migrations/`

## Notes

- Backend and Frontend are separate services
- Both connect to shared database (read/write for backend, read-only for frontend queries)
- Redis is used for BullMQ queue (event ingestion rate limiting)

