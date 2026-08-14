# Day 5, Task 3 — Mentor Submission (Azure Container Apps)

Azure Container Apps fundamentals, resource group creation, environment setup, external ingress, target port, and autoscale rules.

## GitHub Link

https://github.com/thinkbridge-thinkschool/VaishaleeSingh/tree/day5-azure-container-apps/Day5/piece2

---

## Live Azure Provisioning Output

```text
Resource Group: thinkschool-rg (centralindia)
Container App Env: thinkschool-env (Succeeded)
Container App: quotes-api (Active & Healthy)
FQDN: https://quotes-api.whitestone-773df016.centralindia.azurecontainerapps.io
Revision: quotes-api--u33mos7d (Active: True, HealthState: Healthy)
```

## Notes for Mentor

### Provisioning Commands Executed

```powershell
# 1. Create Resource Group
az group create -n thinkschool-rg -l centralindia

# 2. Create Container Apps Environment
az containerapp env create -n thinkschool-env -g thinkschool-rg -l centralindia

# 3. Deploy Container App with External Ingress and HTTP Autoscaling
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

### Key Architectural Decisions

1. **Environment as Boundary (`thinkschool-env`)**:
   - The Container Apps Environment provides the logical security, virtual network, and Log Analytics boundary in Azure (`centralindia`).
2. **Ingress & Target Port Configuration**:
   - `--ingress external` configures public HTTPS ingress backed by Envoy proxy.
   - `--target-port 8080` matches the exposed container port configured in ASP.NET Core (`ContainerPort Include="8080"`).
3. **Autoscaling Strategy**:
   - Integrated KEDA scale rule (`--scale-rule-http-concurrency 50`) scales replicas based on concurrent HTTP requests.
   - Baseline `--min-replicas 1` eliminates cold starts while `--max-replicas 5` caps spending.
4. **Health Probe Integration**:
   - `/health/live` attached to ACA liveness probe (restarts container on process failure).
   - `/health/ready` attached to ACA readiness probe (removes container from Envoy ingress routing during database degradation).

---

## What did you learn this session?

- Azure Container Apps is the serverless successor to traditional App Service plans for containerised microservices.
- ACA combines Kubernetes, KEDA, and Envoy without the operational overhead of managing AKS nodes.
- Revisions enable zero-downtime deployments and blue-green traffic splits out of the box.

---

## What would break this?

- Setting `--min-replicas 0` causes cold-start latency on initial HTTP request arrival.
- Storing SQLite files inside ACA container replicas is ephemeral and fails across scaled instances; external database providers (e.g. Azure SQL / PostgreSQL) are required for stateless cloud container scalability.
