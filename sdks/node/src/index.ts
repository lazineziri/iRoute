export interface TaskConstraints {
  maxInputTokens?: number;
  maxOutputTokens?: number;
  maxCost?: number;
  deadlineMilliseconds?: number;
  minimumQuality?: number;
  requireEvidence?: boolean;
  allowExternalWrites?: boolean;
  maxModelCalls?: number;
  maxToolCalls?: number;
  maxParallelCalls?: number;
  maxTaskDepth?: number;
  allowedRegions?: readonly string[] | null;
  requiredResidency?: string | null;
}

export interface TaskRequest {
  taskType: string;
  input: unknown;
  projectId?: string;
  idempotencyKey?: string;
  constraints?: TaskConstraints;
  metadata?: Readonly<Record<string, string>>;
  tenantId?: string;
  actorId?: string;
  permissionScopes?: readonly string[];
}

export type ExecutionStatus =
  | 'Accepted'
  | 'Resolving'
  | 'Planning'
  | 'Queued'
  | 'WaitingForApproval'
  | 'Running'
  | 'Validating'
  | 'Materializing'
  | 'Compensating'
  | 'Succeeded'
  | 'Failed'
  | 'Cancelled'
  | 'TimedOut';

export type ResolutionLevel =
  | 'ExactArtifact'
  | 'StructuredState'
  | 'SemanticMemory'
  | 'DeterministicCapability'
  | 'SmallModel'
  | 'StrongModel'
  | 'VerifiedOrHuman';

export interface EvidenceReference {
  kind: string;
  reference: string;
  contentHash?: string | null;
  observedAt?: string | null;
}

export interface ArtifactReference {
  artifactId: string;
  artifactType: string;
  version: number;
  contentHash: string;
}

export interface DependencyReference {
  kind: string;
  reference: string;
  contentHash?: string | null;
}

export type ArtifactLifecycleStatus = 'Active' | 'Superseded' | 'Invalidated';
export type MemoryKind = 'Fact' | 'Decision';
export type MemoryLifecycleStatus = 'Active' | 'Superseded' | 'Invalidated';

export interface UsageSummary {
  inputTokens: number;
  outputTokens: number;
  cost: number;
  durationMilliseconds: number;
  modelCalls: number;
  toolCalls: number;
}

export interface ValidationSummary {
  passed: boolean;
  quality: number;
  checks: readonly string[];
  failures: readonly string[];
}

export interface ContextManifestEntry {
  kind: string;
  reference: string;
  included: boolean;
  reason: string;
  estimatedTokens: number;
  contentHash?: string | null;
  rank: number;
  outputPath: string | null;
}

export interface ContextManifest {
  estimatedTokens: number;
  budgetTokens: number;
  projectedInputTokens: number;
  contextTokens: number;
  truncated: boolean;
  fullHistoryIncluded: boolean;
  entries: readonly ContextManifestEntry[];
  provenance: Readonly<Record<string, EvidenceReference>>;
}

export type RoutingPath = 'Direct' | 'Workflow';
export type ModelTier = 'Small' | 'Strong' | 'Verifier';

export interface RoutingCandidateEvaluation {
  capability: string;
  profileId: string | null;
  modelTier: ModelTier | null;
  eligible: boolean;
  reason: string;
  expectedQuality: number;
  expectedCost: number;
  expectedLatencyMilliseconds: number;
  uncertainty: number;
  reliability: number;
  availability: number;
  score: number;
}

export interface RoutingDecision {
  policyVersion: string;
  path: RoutingPath;
  reason: string;
  selectedCapability: string;
  selectedProfileId: string | null;
  selectedModelTier: ModelTier | null;
  qualityFloor: number;
  expectedQuality: number;
  expectedCost: number;
  expectedLatencyMilliseconds: number;
  uncertainty: number;
  score: number;
  plannerInvoked: boolean;
  planningCalls: number;
  escalated: boolean;
  escalationReason: string | null;
  candidates: readonly RoutingCandidateEvaluation[];
  selectedDeployment?: GatewayDeploymentReference | null;
  resilience?: GatewayResilienceTrace | null;
}

export interface ModelProfile {
  profileId: string;
  capability: string;
  tier: ModelTier;
  supportedTaskTypes: readonly string[];
  expectedQuality: number;
  estimatedCost: number;
  expectedLatencyMilliseconds: number;
  uncertainty: number;
  reliability: number;
  availability: number;
  maxInputTokens: number;
  maxOutputTokens: number;
  healthy: boolean;
  measurementSource: string;
}

export type ModelGatewayHealthStatus = 'Healthy' | 'Degraded' | 'Unavailable';
export type ModelGatewayTransport = 'Buffered' | 'Streaming';
export type ModelGatewayFinishReason =
  | 'Completed'
  | 'Length'
  | 'ContentFiltered'
  | 'ToolCall'
  | 'Other';
export type ModelGatewayStreamEventKind = 'OutputDelta' | 'Usage' | 'Completed';
export type ModelGatewayFailureKind =
  | 'InvalidRequest'
  | 'Authentication'
  | 'RateLimited'
  | 'Timeout'
  | 'Unavailable'
  | 'InvalidResponse'
  | 'Cancelled'
  | 'Internal';

export type GatewayFailureClass =
  | 'Timeout'
  | 'Throttling'
  | 'Transport'
  | 'Provider'
  | 'MalformedOutput'
  | 'Validation'
  | 'Policy'
  | 'Permanent';
export type GatewayCircuitState = 'Closed' | 'Open' | 'HalfOpen';

export interface GatewayDeploymentReference {
  gatewayId: string;
  provider: string;
  deploymentId: string;
  region: string;
  residency: string;
  modelVersion: string;
}

export interface GatewayCandidateEvidence {
  deployment: GatewayDeploymentReference;
  eligible: boolean;
  reason: string;
  circuitState: GatewayCircuitState;
  failureClass: GatewayFailureClass | null;
}

export interface GatewayAttemptEvidence {
  deployment: GatewayDeploymentReference;
  attempt: number;
  circuitStateBefore: GatewayCircuitState;
  circuitStateAfter: GatewayCircuitState;
  succeeded: boolean;
  failureClass: GatewayFailureClass | null;
  failureCode: string | null;
  statusCode: number | null;
  durationMilliseconds: number;
  retryAfterMilliseconds: number | null;
}

export interface GatewayResilienceTrace {
  policyVersion: string;
  candidates: readonly GatewayCandidateEvidence[];
  attempts: readonly GatewayAttemptEvidence[];
  finalDeployment: GatewayDeploymentReference | null;
  fallbackReason: string | null;
  exhaustionReason: string | null;
}

export interface ModelGatewayRequest {
  capability: string;
  input: unknown;
  context: unknown;
  maxOutputTokens: number;
  correlationId?: string | null;
  profileId?: string | null;
  deadlineMilliseconds?: number | null;
  minimumQuality?: number | null;
  maximumCost?: number | null;
  allowedRegions?: readonly string[] | null;
  requiredResidency?: string | null;
  maximumAttempts?: number | null;
}

export interface ModelGatewayResult {
  output: unknown;
  usage: UsageSummary;
  confidence: number;
  evidence: readonly EvidenceReference[];
  gatewayId?: string | null;
  transport?: ModelGatewayTransport;
  finishReason?: ModelGatewayFinishReason;
  deployment?: GatewayDeploymentReference | null;
  resilience?: GatewayResilienceTrace | null;
}

export interface ModelGatewayStreamEvent {
  sequence: number;
  kind: ModelGatewayStreamEventKind;
  delta?: string | null;
  usage?: UsageSummary | null;
  result?: ModelGatewayResult | null;
}

export interface ModelGatewayFailure {
  code: string;
  kind: ModelGatewayFailureKind;
  message: string;
  retryable: boolean;
  statusCode?: number | null;
  gatewayId?: string | null;
  correlationId?: string | null;
  retryAfterMilliseconds?: number | null;
  failureClass?: GatewayFailureClass | null;
  resilience?: GatewayResilienceTrace | null;
}

export interface ModelGatewayHealth {
  gatewayId: string;
  status: ModelGatewayHealthStatus;
  latencyMilliseconds: number;
  checkedAt: string;
  message?: string | null;
}

export type CapabilityKind = 'Deterministic' | 'Model' | 'Tool' | 'Api' | 'Mcp' | 'Agent';
export type CapabilityCacheability = 'Never' | 'Scoped' | 'Public';
export type CapabilityDataSensitivity = 'Public' | 'Internal' | 'Confidential' | 'Restricted';
export type CapabilityTrustLevel = 'Internal' | 'Registered' | 'ExternalUntrusted';
export type CapabilityIsolation = 'InProcess' | 'Process' | 'Container' | 'Remote';
export type CapabilityFailureKind =
  | 'InvalidRequest'
  | 'PermissionDenied'
  | 'NotRegistered'
  | 'Timeout'
  | 'Unavailable'
  | 'InvalidResponse'
  | 'OutputLimitExceeded'
  | 'Cancelled'
  | 'Internal';

export interface CapabilityDefinition {
  capability: string;
  version: number;
  kind: CapabilityKind;
  description: string;
  inputSchema: Readonly<Record<string, unknown>>;
  outputSchema: Readonly<Record<string, unknown>>;
  sideEffectClass: 'None' | 'ReadOnly' | 'ReversibleWrite' | 'IrreversibleWrite';
  authenticationScopes: readonly string[];
  permissionScopes: readonly string[];
  idempotency: { supported: boolean; requiredForWrites: boolean };
  retry: { maxAttempts: number; retryableFailureClasses: readonly string[] };
  serviceProfile: {
    expectedLatencyMilliseconds: number;
    estimatedCost: number;
    availability: number;
    reliability: number;
  };
  cacheability: CapabilityCacheability;
  freshnessSeconds: number | null;
  dataSensitivity: CapabilityDataSensitivity;
  trustLevel: CapabilityTrustLevel;
  isolation: CapabilityIsolation;
}

export interface CapabilityInvocationRequest {
  capability: string;
  version: number;
  input: unknown;
  tenantId: string;
  actorId: string;
  projectId: string | null;
  permissionScopes: readonly string[];
  policyVersion: string;
  sideEffectClass: 'None' | 'ReadOnly' | 'ReversibleWrite' | 'IrreversibleWrite';
  deadlineMilliseconds: number;
  maximumOutputBytes: number;
  correlationId: string;
  idempotencyReference?: string | null;
}

export interface CapabilityExecutionMetadata {
  capability: string;
  version: number;
  connectorId: string;
  kind: CapabilityKind;
  trustLevel: CapabilityTrustLevel;
  transport: string;
  projected: true;
  outputReference: string;
}

export interface CapabilityInvocationResult {
  output: unknown;
  usage: UsageSummary;
  confidence: number;
  evidence: readonly EvidenceReference[];
  metadata: CapabilityExecutionMetadata;
}

export interface CapabilityFailure {
  code: string;
  kind: CapabilityFailureKind;
  message: string;
  retryable: boolean;
  capability?: string | null;
  connectorId?: string | null;
  correlationId?: string | null;
}

export interface TaskOutcome {
  output: unknown;
  resolutionLevel: ResolutionLevel;
  confidence: number;
  evidence: readonly EvidenceReference[];
  usage: UsageSummary;
  artifacts: readonly ArtifactReference[];
  validation?: ValidationSummary | null;
  context?: ContextManifest | null;
  routing?: RoutingDecision | null;
}

export interface Problem {
  code: string;
  title: string;
  detail: string;
  retryable: boolean;
  metadata?: Readonly<Record<string, string>> | null;
}

export interface ExecutionSnapshot {
  executionId: string;
  taskType: string;
  status: ExecutionStatus;
  createdAt: string;
  updatedAt: string;
  outcome?: TaskOutcome | null;
  error?: Problem | null;
  tenantId: string;
  actorId: string;
  projectId?: string | null;
  taskDefinitionVersion?: number | null;
  cancellationRequestedAt?: string | null;
}

export interface ArtifactSnapshot {
  artifact: ArtifactReference;
  tenantId: string;
  projectId?: string | null;
  taskType: string;
  taskDefinitionVersion: number;
  content: unknown;
  evidence: readonly EvidenceReference[];
  createdAt: string;
  expiresAt?: string | null;
  isActive: boolean;
  logicalKey?: string | null;
  lifecycleStatus?: ArtifactLifecycleStatus;
  supersedesArtifactId?: string | null;
  supersededByArtifactId?: string | null;
  dependencies?: readonly DependencyReference[] | null;
  invalidatedAt?: string | null;
  invalidationReason?: string | null;
}

export interface MemorySnapshot {
  memoryId: string;
  tenantId: string;
  projectId?: string | null;
  kind: MemoryKind;
  key: string;
  version: number;
  value: unknown;
  contentHash: string;
  lifecycleStatus: MemoryLifecycleStatus;
  evidence: readonly EvidenceReference[];
  dependencies: readonly DependencyReference[];
  createdAt: string;
  expiresAt?: string | null;
  supersedesMemoryId?: string | null;
  supersededByMemoryId?: string | null;
  invalidatedAt?: string | null;
  invalidationReason?: string | null;
}

export interface ExecutionEvent {
  sequence: number;
  executionId: string;
  type: string;
  occurredAt: string;
  data: unknown;
}

export interface ResolutionConsideration {
  resolver: string;
  accepted: boolean;
  code: ResolutionDecisionCode;
  reason: string;
  permissionChecked: boolean;
  freshnessChecked: boolean;
  checks: number;
  level?: ResolutionLevel | null;
}

export type ResolutionDecisionCode =
  | 'exact_cache_hit'
  | 'exact_cache_miss'
  | 'permission_denied'
  | 'unsupported_task'
  | 'project_scope_required'
  | 'state_key_required'
  | 'state_hit'
  | 'state_unavailable'
  | 'artifact_reference_required'
  | 'artifact_hit'
  | 'artifact_unavailable'
  | 'handler_unavailable'
  | 'handler_declined'
  | 'handler_stale'
  | 'handler_accepted'
  | 'external_write_blocked'
  | 'validation_failed';

export type ApprovalStatus = 'Pending' | 'Approved' | 'Denied';

export interface ApprovalDecision {
  actionId: string;
  approved: boolean;
  reason?: string | null;
}

export interface ApprovalSnapshot {
  executionId: string;
  actionId: string;
  status: ApprovalStatus;
  capability: string;
  sideEffectClass: 'None' | 'ReadOnly' | 'ReversibleWrite' | 'IrreversibleWrite';
  requiredPermissionScopes: readonly string[];
  requestedByActorId: string;
  decidedByActorId?: string | null;
  inputReference: string;
  idempotencyReference: string;
  createdAt: string;
  decidedAt?: string | null;
  reason?: string | null;
}

export interface ApprovalResult {
  approval: ApprovalSnapshot;
  execution: ExecutionSnapshot;
}

export interface ObservabilityMetricSet {
  executions: number;
  completed: number;
  failed: number;
  completionRate: number;
  averageQuality: number;
  inputTokens: number;
  outputTokens: number;
  cost: number;
  averageLatencyMilliseconds: number;
  modelCalls: number;
  toolCalls: number;
  noModelResolutions: number;
  modelCallsAvoided: number;
}

export interface ObservabilityMetricGroup {
  taskType: string;
  policyVersion: string;
  metrics: ObservabilityMetricSet;
}

export interface GatewayObservabilityMetricSet {
  attempts: number;
  successes: number;
  failures: number;
  rejectedCandidates: number;
  fallbacks: number;
  averageLatencyMilliseconds: number;
}

export interface GatewayObservabilityMetricGroup {
  gatewayId: string;
  provider: string;
  deploymentId: string;
  region: string;
  modelVersion: string;
  failureClass: GatewayFailureClass | null;
  circuitState: GatewayCircuitState;
  metrics: GatewayObservabilityMetricSet;
}

export interface MemoryResolverDiagnostic {
  resolver: string;
  considered: number;
  accepted: number;
  rejected: number;
  reasons: Readonly<Record<string, number>>;
}

export interface MemoryHitDiagnostics {
  considered: number;
  accepted: number;
  rejected: number;
  hitRate: number;
  resolvers: readonly MemoryResolverDiagnostic[];
}

export interface ObservabilityExecutionItem {
  executionId: string;
  taskType: string;
  policyVersion: string;
  status: ExecutionStatus;
  startedAt: string;
  durationMilliseconds: number;
  resolutionLevel: ResolutionLevel | null;
  quality: number | null;
  cost: number;
  inputTokens: number;
  outputTokens: number;
}

export interface ObservabilitySummary {
  from: string;
  to: string;
  truncated: boolean;
  sampledExecutions: number;
  totals: ObservabilityMetricSet;
  groups: readonly ObservabilityMetricGroup[];
  memory: MemoryHitDiagnostics;
  recentExecutions: readonly ObservabilityExecutionItem[];
  gatewayGroups?: readonly GatewayObservabilityMetricGroup[];
}

export type ObservabilityPayloadMode = 'MetadataOnly' | 'Redacted';

export interface ExecutionTimelineEntry {
  sequence: number;
  type: string;
  occurredAt: string;
  offsetMilliseconds: number;
  data: Readonly<Record<string, unknown>>;
}

export interface ExecutionTimeline {
  executionId: string;
  traceId: string;
  taskType: string;
  taskDefinitionVersion: number | null;
  policyVersion: string;
  status: ExecutionStatus;
  startedAt: string;
  updatedAt: string;
  durationMilliseconds: number;
  actorReference: string;
  projectReference: string | null;
  payloadMode: ObservabilityPayloadMode;
  truncated: boolean;
  events: readonly ExecutionTimelineEntry[];
}

export interface ObservabilityQuery {
  from?: string | Date;
  to?: string | Date;
  taskType?: string;
  policyVersion?: string;
}

export interface IRouteClientOptions {
  token?: string;
  tenantId?: string;
  actorId?: string;
  permissionScopes?: readonly string[];
  fetch?: typeof globalThis.fetch;
}

export class IRouteApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string | undefined,
    public readonly title: string | undefined,
    public readonly detail: string | undefined,
    public readonly responseBody: string
  ) {
    super(detail || title || `iRoute request failed with HTTP ${status}`);
    this.name = 'IRouteApiError';
  }
}

export class IRouteClient {
  private readonly transport: typeof globalThis.fetch;

  constructor(
    private readonly baseUrl: URL,
    private readonly options: IRouteClientOptions = {}
  ) {
    this.transport = options.fetch ?? globalThis.fetch;
  }

  async execute(request: TaskRequest, signal?: AbortSignal): Promise<ExecutionSnapshot> {
    const response = await this.transport(new URL('/v1/executions', this.baseUrl), this.requestInit({
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        ...(request.idempotencyKey ? { 'idempotency-key': request.idempotencyKey } : {}),
        ...(request.tenantId ? { 'x-tenant-id': request.tenantId } : {}),
        ...(request.actorId ? { 'x-actor-id': request.actorId } : {}),
        ...(request.permissionScopes?.length
          ? { 'x-permission-scopes': request.permissionScopes.join(' ') }
          : {})
      },
      body: JSON.stringify(request),
      ...(signal ? { signal } : {})
    }));
    return await this.readJson<ExecutionSnapshot>(response);
  }

  async get(executionId: string, signal?: AbortSignal): Promise<ExecutionSnapshot | undefined> {
    const response = await this.transport(
      new URL(`/v1/executions/${encodeURIComponent(executionId)}`, this.baseUrl),
      this.requestInit({ method: 'GET', ...(signal ? { signal } : {}) })
    );
    if (response.status === 404) return undefined;
    return await this.readJson<ExecutionSnapshot>(response);
  }

  async cancel(executionId: string, signal?: AbortSignal): Promise<boolean> {
    const response = await this.transport(
      new URL(`/v1/executions/${encodeURIComponent(executionId)}/cancel`, this.baseUrl),
      this.requestInit({ method: 'POST', ...(signal ? { signal } : {}) })
    );
    if (response.status === 404) return false;
    if (!response.ok) throw await this.createError(response);
    return true;
  }

  async submitApproval(
    executionId: string,
    decision: ApprovalDecision,
    signal?: AbortSignal
  ): Promise<ApprovalResult> {
    const response = await this.transport(
      new URL(`/v1/executions/${encodeURIComponent(executionId)}/approvals`, this.baseUrl),
      this.requestInit({
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(decision),
        ...(signal ? { signal } : {})
      })
    );
    return await this.readJson<ApprovalResult>(response);
  }

  async getArtifact(artifactId: string, signal?: AbortSignal): Promise<ArtifactSnapshot | undefined> {
    const response = await this.transport(
      new URL(`/v1/artifacts/${encodeURIComponent(artifactId)}`, this.baseUrl),
      this.requestInit({ method: 'GET', ...(signal ? { signal } : {}) })
    );
    if (response.status === 404) return undefined;
    return await this.readJson<ArtifactSnapshot>(response);
  }

  async getModelGatewayHealth(signal?: AbortSignal): Promise<ModelGatewayHealth> {
    const response = await this.transport(
      new URL('/health/model-gateway', this.baseUrl),
      this.requestInit({ method: 'GET', ...(signal ? { signal } : {}) })
    );
    if (response.status !== 200 && response.status !== 503) {
      throw await this.createError(response);
    }
    return await response.json() as ModelGatewayHealth;
  }

  async getObservabilitySummary(
    query: ObservabilityQuery = {},
    signal?: AbortSignal
  ): Promise<ObservabilitySummary> {
    const url = new URL('/v1/observability/summary', this.baseUrl);
    if (query.from) url.searchParams.set('from', toTimestamp(query.from));
    if (query.to) url.searchParams.set('to', toTimestamp(query.to));
    if (query.taskType) url.searchParams.set('taskType', query.taskType);
    if (query.policyVersion) url.searchParams.set('policyVersion', query.policyVersion);
    const response = await this.transport(
      url,
      this.requestInit({ method: 'GET', ...(signal ? { signal } : {}) })
    );
    return await this.readJson<ObservabilitySummary>(response);
  }

  async getExecutionTimeline(
    executionId: string,
    signal?: AbortSignal
  ): Promise<ExecutionTimeline | undefined> {
    const response = await this.transport(
      new URL(`/v1/observability/executions/${encodeURIComponent(executionId)}`, this.baseUrl),
      this.requestInit({ method: 'GET', ...(signal ? { signal } : {}) })
    );
    if (response.status === 404) return undefined;
    return await this.readJson<ExecutionTimeline>(response);
  }

  async *streamEvents(
    executionId: string,
    afterSequence = 0,
    signal?: AbortSignal
  ): AsyncGenerator<ExecutionEvent> {
    const url = new URL(`/v1/executions/${encodeURIComponent(executionId)}/events`, this.baseUrl);
    url.searchParams.set('after', afterSequence.toString());
    const response = await this.transport(url, this.requestInit({
      method: 'GET',
      headers: { accept: 'text/event-stream' },
      ...(signal ? { signal } : {})
    }));
    if (!response.ok) throw await this.createError(response);
    if (!response.body) throw new Error('iRoute returned an empty event stream.');

    const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
    let buffer = '';
    try {
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += value.replaceAll('\r\n', '\n');
        let boundary = buffer.indexOf('\n\n');
        while (boundary >= 0) {
          const block = buffer.slice(0, boundary);
          buffer = buffer.slice(boundary + 2);
          const event = parseSseBlock(block);
          if (event) yield event;
          boundary = buffer.indexOf('\n\n');
        }
      }
      const finalEvent = parseSseBlock(buffer);
      if (finalEvent) yield finalEvent;
    } finally {
      reader.releaseLock();
    }
  }

  private requestInit(init: RequestInit): RequestInit {
    const headers = new Headers(init.headers);
    if (this.options.token) headers.set('authorization', `Bearer ${this.options.token}`);
    if (this.options.tenantId && !headers.has('x-tenant-id')) {
      headers.set('x-tenant-id', this.options.tenantId);
    }
    if (this.options.actorId && !headers.has('x-actor-id')) {
      headers.set('x-actor-id', this.options.actorId);
    }
    if (this.options.permissionScopes?.length && !headers.has('x-permission-scopes')) {
      headers.set('x-permission-scopes', this.options.permissionScopes.join(' '));
    }
    return { ...init, headers };
  }

  private async readJson<T>(response: Response): Promise<T> {
    if (!response.ok) throw await this.createError(response);
    return await response.json() as T;
  }

  private async createError(response: Response): Promise<IRouteApiError> {
    const responseBody = await response.text();
    let problem: { code?: string; title?: string; detail?: string } = {};
    try {
      problem = JSON.parse(responseBody) as typeof problem;
    } catch {}
    return new IRouteApiError(
      response.status,
      problem.code,
      problem.title,
      problem.detail,
      responseBody
    );
  }
}

function toTimestamp(value: string | Date): string {
  return value instanceof Date ? value.toISOString() : value;
}

function parseSseBlock(block: string): ExecutionEvent | undefined {
  const data = block
    .replaceAll('\r\n', '\n')
    .split('\n')
    .filter(line => line.startsWith('data:'))
    .map(line => line.slice(5).trimStart())
    .join('\n');
  return data ? JSON.parse(data) as ExecutionEvent : undefined;
}
