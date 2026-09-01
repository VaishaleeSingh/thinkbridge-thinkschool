// Day 19 — Azure Service Bus topology: namespace, topic, subscriptions, rules.
//
// WHY BICEP OWNS THIS (not the application):
// An app that creates its own topics needs Manage rights in production, which is
// exactly the right it should not have. Infrastructure declares the topology;
// the app only gets Sender on the topic and Receiver on the subscriptions.
//
// Named resources produced by this file:
//   quotes-<env>.servicebus.windows.net            — the namespace
//   quote-events                                   — the topic
//   audit           (TrueFilter on All)            — subscription 1
//   search-index    (SQL: eventType IN ('QuoteCreated','QuoteUpdated'))  — subscription 2
//
// IMPORTANT: the search-index subscription's default '$Default' TrueFilter
// is REMOVED when the SQL filter is added. Adding a rule does not replace the
// default one, and a subscription with both matches everything — this is the
// single most common "my filter does nothing" bug.

targetScope = 'resourceGroup'

@description('Short environment name, e.g. dev | staging | prod')
param env string

@description('Principal ID of the application managed identity that needs send/receive rights')
param appPrincipalId string

@description('Location for all resources')
param location string = resourceGroup().location

// ------------------------------------------------------------------
// Service Bus Namespace  (Standard tier required for topics)
// ------------------------------------------------------------------
// IMPORTANT: Basic tier has queues only. Topics and subscriptions require
// Standard or Premium. Getting this wrong produces a clear error at
// deployment time, not at runtime.
resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: 'quotes-${env}'
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    // Disable local (SAS) auth: managed identity only.
    // The app carries DefaultAzureCredential; no connection string anywhere.
    disableLocalAuth: true
  }
}

// ------------------------------------------------------------------
// Topic: quote-events
// ------------------------------------------------------------------
resource topic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  name: 'quote-events'
  parent: namespace
  properties: {
    // A 7-day TTL on the topic bounds the "unbounded bill" risk: an
    // unread subscription accumulates at most 7 days of messages, not
    // all messages since the beginning of time.
    defaultMessageTimeToLive: 'P7D'

    // Duplicate detection OFF (deliberate — see §4 of the implementation
    // plan). Broker-side dup detection protects against a publisher
    // sending twice; it does NOT protect the consumer against redelivery
    // after a lock expiry or crash. The consumer-side ProcessedMessages
    // table is the guarantee that actually holds.
    requiresDuplicateDetection: false

    enableBatchedOperations: true
    supportOrdering: false
  }
}

// ------------------------------------------------------------------
// Subscription: audit  (receives ALL event types via TrueFilter)
// ------------------------------------------------------------------
resource auditSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  name: 'audit'
  parent: topic
  properties: {
    // MaxDeliveryCount = 3 (the service default is 10): the default hides a
    // poison message behind ten attempts. How long those attempts take
    // depends on how the consumer fails — an explicit AbandonMessageAsync
    // redelivers immediately, while a consumer that dies holding the lock
    // costs one LockDuration (PT1M here) per attempt. Three is enough to
    // absorb a transient blip and few enough to reach the DLQ promptly.
    maxDeliveryCount: 3

    // LockDuration PT1M: long enough for the handler, short enough that
    // a crashed consumer's message returns quickly. Default is PT1M, but
    // stating it explicitly means the reader knows it was considered.
    lockDuration: 'PT1M'

    defaultMessageTimeToLive: 'P7D'

    // Dead-letter on expiration: an expired audit event should be
    // inspectable (and potentially replayed), not silently dropped.
    deadLetteringOnMessageExpiration: true

    enableBatchedOperations: true
  }
}

// The audit subscription keeps the default '$Default' TrueFilter,
// which is added automatically by Service Bus. No rule resource needed.

// ------------------------------------------------------------------
// Subscription: search-index  (Created + Updated only, not Deleted)
// ------------------------------------------------------------------
resource searchIndexSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  name: 'search-index'
  parent: topic
  properties: {
    maxDeliveryCount: 3
    lockDuration: 'PT1M'
    defaultMessageTimeToLive: 'P7D'
    deadLetteringOnMessageExpiration: true
    enableBatchedOperations: true
  }
}

// Remove the default '$Default' TrueFilter first, then add the SQL filter.
// CRITICAL: Adding a rule does NOT replace '$Default'. A subscription with
// BOTH matches everything — your filter does nothing.
resource removeDefaultRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = {
  name: '$Default'
  parent: searchIndexSubscription
  properties: {
    // Precisely: this OVERWRITES '$Default', it does not delete it. ARM has
    // no "remove this rule" verb, so the default TrueFilter is redefined as
    // a filter that matches nothing. Rules on a subscription are OR'd, so
    // the effective filter becomes (1=0 OR eventType IN (...)) — which is
    // the SQL filter alone. Leaving '$Default' as its original TrueFilter
    // would instead make the effective filter (true OR ...) = everything,
    // which is the "my filter does nothing" bug in one line.
    //
    // Deleting the rule outright is possible with `az servicebus topic
    // subscription rule delete`, but a deployment that is not idempotent is
    // a worse trade than a rule that matches nothing.
    filterType: 'SqlFilter'
    sqlFilter: {
      sqlExpression: '1=0'
    }
  }
}

resource contentChangesRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = {
  name: 'content-changes-only'
  parent: searchIndexSubscription
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: {
      sqlExpression: 'eventType IN (\'QuoteCreated\',\'QuoteUpdated\')'
    }
  }
  dependsOn: [removeDefaultRule]
}

// ------------------------------------------------------------------
// RBAC: managed identity gets Sender on the topic and Receiver on subs
// ------------------------------------------------------------------
// No connection strings, no shared-access policies on the app side.
// Matching exactly how Key Vault is wired in Program.cs.

var senderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39' // Azure Service Bus Data Sender
var receiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0' // Azure Service Bus Data Receiver

resource topicSenderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(namespace.id, appPrincipalId, senderRoleId)
  scope: topic
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', senderRoleId)
    principalId: appPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource auditReceiverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(namespace.id, appPrincipalId, receiverRoleId, 'audit')
  scope: auditSubscription
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', receiverRoleId)
    principalId: appPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource searchIndexReceiverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(namespace.id, appPrincipalId, receiverRoleId, 'search-index')
  scope: searchIndexSubscription
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', receiverRoleId)
    principalId: appPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ------------------------------------------------------------------
// Outputs — consumed by the app's configuration pipeline
// ------------------------------------------------------------------
output namespaceFqdn string = '${namespace.name}.servicebus.windows.net'
output topicName string = topic.name
output auditSubscriptionName string = auditSubscription.name
output searchIndexSubscriptionName string = searchIndexSubscription.name
