# Azure Container Apps Fundamentals for ASP.NET Core (.NET 10)

Azure Container Apps (ACA) is the modern (2026+) default serverless platform for running containerised ASP.NET workloads on Azure. Built on top of Kubernetes, KEDA (Kubernetes Event-driven Autoscaling), Envoy proxy, and Dapr, ACA abstracts away cluster management while offering native microservice features like scale-to-zero, traffic splitting across revisions, and built-in observability.

---

## 1. Core Architecture & Resource Model

```
                    ┌───────────────────────────────────────────────┐
                    │            Azure Resource Group               │
                    │             (thinkschool-rg)                  │
                    │                                               │
                    │  ┌─────────────────────────────────────────┐  │
                    │  │   Container Apps Environment            │  │
                    │  │        (thinkschool-env)                │  │
                    │  │  ┌──────────────────────────────────┐   │  │
                    │  │  │   Container App: quotes-api      │   │  │
                    │  │  │                                  │   │  │
                    │  │  │   Revision 1: quotes-api--v1    │   │  │
                    │  │  │   Revision 2: quotes-api--v2    │   │  │
                    │  │  │   Ingress: External (Port 8080)  │   │  │
                    │  │  └──────────────────────────────────┘   │  │
                    │  └─────────────────────────────────────────┘  │
                    └───────────────────────────────────────────────┘
```

### Key Concepts:

1. **Resource Group (`thinkschool-rg`)**: The Azure management container holding all related resources.
2. **Container Apps Environment (`thinkschool-env`)**: A logical boundary surrounding one or more Container Apps. Apps within the same environment share the same Virtual Network (VNet), Log Analytics workspace, and Dapr configuration.
3. **Container App (`quotes-api`)**: The top-level resource representing your application service.
4. **Revisions**: Immutable snapshots of a container app version (image + configuration + scale rules). Multiple revisions can run simultaneously to support **Blue/Green deployments**, **Canary releases**, or **Instant rollbacks**.
5. **Replicas**: Running instances of a specific revision, automatically scaled up or down by KEDA.

---

## 2. Azure App Service vs. Azure Container Apps

| Feature                       | Azure App Service                           | Azure Container Apps                           |
| ----------------------------- | ------------------------------------------- | ---------------------------------------------- |
| **Underlying Tech**           | App Service Plans (VM worker pools)         | Serverless Kubernetes (K8s + KEDA + Envoy)     |
| **Scale to Zero**             | ❌ Requires Consumption plan / high latency | ✅ Native (0 vCPU cost when idle)              |
| **Billing Model**             | Pay per provisioned VM hour                 | Pay per vCPU-second and Memory-second used     |
| **Autoscaling**               | CPU/Memory metrics (slow reaction)          | KEDA (HTTP requests, Event Queues, CPU/Memory) |
| **Revisions & Traffic Split** | Staging slots (swaps)                       | Unlimited immutable revisions & exact % splits |
| **Containers**                | Supported                                   | Native OCI-first design                        |

---

## 3. Azure CLI Provisioning Workflow

### Step 1: Create Resource Group

```powershell
az group create -n thinkschool-rg -l centralindia
```

- `-n thinkschool-rg`: Names the logical resource group.
- `-l centralindia`: Specifies the Azure region (`centralindia`).

---

### Step 2: Create Container Apps Environment

```powershell
az containerapp env create -n thinkschool-env -g thinkschool-rg -l centralindia
```

- `-n thinkschool-env`: Names the shared environment boundary.
- `-g thinkschool-rg`: Binds the environment to our resource group.
- `-l centralindia`: Ensures co-location of networking and logging infrastructure.

---

### Step 3: Create Container App with Ingress, Health Probes & Scale Rules

```powershell
az containerapp create `
  --name quotes-api `
  --resource-group thinkschool-rg `
  --environment thinkschool-env `
  --image quotes-api:0.1.0 `
  --ingress external `
  --target-port 8080 `
  --min-replicas 1 `
  --max-replicas 5 `
  --scale-rule-name http-rule `
  --scale-rule-type http `
  --scale-rule-http-concurrency 50 `
  --env-vars Jwt__Secret="SuperSecretKeyForJwtAuthenticationMustBeAtLeast32BytesLong!"
```

#### Detailed Explanation of Flags:

- `--ingress external`: Configures an external-facing Envoy proxy with an automatically provisioned TLS/HTTPS endpoint accessible via public DNS (`https://quotes-api.<env-hash>.centralindia.azurecontainerapps.io`).
- `--target-port 8080`: Instructs the Envoy ingress proxy to forward HTTP traffic to port `8080` inside the container (matching ASP.NET Core container default port configuration).
- `--scale-rule-name http-rule` & `--scale-rule-type http`: Attaches a KEDA HTTP scaler.
- `--scale-rule-http-concurrency 50`: Automatically provisions an extra replica whenever active HTTP requests per container instance exceed `50`.
- `--min-replicas 1`: Maintains at least 1 warm replica to avoid cold-start latency. Setting to `0` enables scale-to-zero for maximum cost efficiency.
- `--max-replicas 5`: Caps scaling at 5 replicas to prevent unexpected cloud spend spikes.
- `--env-vars Jwt__Secret=...`: Injects the required JWT secret key directly as an environment variable (`Jwt__Secret`), matching .NET configuration key syntax (`Jwt:Secret`).

---

## 4. Health Probes Configuration (Liveness & Readiness)

In production ACA deployments, health probes ensure proper container lifecycle management:

```yaml
# ACA YAML Manifest snippet for Health Probes
properties:
  template:
    containers:
      - name: quotes-api
        image: quotes-api:0.1.0
        probes:
          - type: liveness
            httpGet:
              path: /health/live
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 10
          - type: readiness
            httpGet:
              path: /health/ready
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 10
```

- **Liveness Probe (`/health/live`)**: Answers whether the ASP.NET Core process is running. If it fails, ACA restarts the container instance. Does **not** check external dependencies.
- **Readiness Probe (`/health/ready`)**: Answers whether the container is ready to accept HTTP traffic (verifies database connectivity). If it fails, Envoy stops routing traffic to this replica without restarting it.

---

## 5. Revisions & Traffic Splitting (Blue-Green Deployments)

Revisions are immutable snapshots. When a new container image tag or configuration change is deployed:

```powershell
# Deploy a new revision (v0.2.0)
az containerapp update `
  --name quotes-api `
  --resource-group thinkschool-rg `
  --image quotes-api:0.2.0

# Split traffic 80% to v1 and 20% to v2 (Canary Deployment)
az containerapp ingress traffic set `
  --name quotes-api `
  --resource-group thinkschool-rg `
  --revision-weight quotes-api--v1=80 quotes-api--v2=20
```

---

## 6. What Would Break This? (Failure Modes & Mitigation)

1. **Cold Starts when `--min-replicas 0`**: If scaling to zero, the first incoming HTTP request incurs a cold-start delay (1-3 seconds) while the container image spins up. _Mitigation_: Keep `--min-replicas 1` for latency-sensitive APIs.
2. **Ephemeral Database Storage**: SQLite running inside an ACA replica writes to `/tmp/quotes.db`. If the replica scales to 0 or restarts, local data is erased, and multiple replicas do not share state. _Mitigation_: Use Azure SQL Database or PostgreSQL via connection strings.
3. **Database Migration Concurrency**: Running `MigrateAsync()` on startup across 5 parallel replicas during scaling can cause schema lock conflicts. _Mitigation_: Run EF Core migrations in a CI/CD deployment pipeline step prior to ACA container rollout.
