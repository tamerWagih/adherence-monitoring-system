# VM Setup Commands

## 1. Clone Repository

```bash
cd /path/to/your/projects
git clone https://github.com/tamerWagih/adherence-monitoring-system.git
cd adherence-monitoring-system
```

## 2. Create .env File from Template

```bash
cp .env.template .env
```

## 3. Edit .env File

```bash
nano .env
# or
vi .env
```

Update the following values:
- `DATABASE_PASSWORD=ops@1234` (or your actual password)
- `REDIS_PASSWORD=` (if Redis has password, or leave empty)
- `JWT_SECRET=` (generate a secure random string)
- `CORS_ORIGIN=http://YOUR_VM_IP` (replace YOUR_VM_IP with actual IP)
- `NEXT_PUBLIC_API_URL=http://YOUR_VM_IP/api/adherence` (replace YOUR_VM_IP with actual IP)

## 4. Generate JWT Secret (Optional)

```bash
# Generate a secure random string for JWT_SECRET
openssl rand -base64 32
```

## 5. Verify .env is Ignored

```bash
# Check if .env is in .gitignore (should already be there)
grep -n "\.env" .gitignore
```

## 6. Verify Setup

```bash
# Check that .env file exists and is not tracked by git
ls -la .env
git status
# .env should NOT appear in git status
```

## 7. Build and Start Services

```bash
# Build Docker images
docker-compose build

# Start services
docker-compose up -d

# Check logs
docker-compose logs -f

# Check service status
docker-compose ps
```

## 8. Verify Services are Running

```bash
# Check backend health
curl http://localhost/api/adherence/health

# Check nginx health
curl http://localhost/health

# Check all containers
docker ps
```

