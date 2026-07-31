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
}

export interface ContextManifest {
  estimatedTokens: number;
  budgetTokens: number;
  truncated: boolean;
  entries: readonly ContextManifestEntry[];
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

export interface IRouteClientOptions {
  token?: string;
  tenantId?: string;
  actorId?: string;
  permissionScopes?: readonly string[];
}

export class IRouteClient {
  constructor(
    private readonly baseUrl: URL,
    private readonly options: IRouteClientOptions = {}
  ) {}

  async execute(request: TaskRequest, signal?: AbortSignal): Promise<ExecutionSnapshot> {
    const response = await fetch(new URL('/v1/executions', this.baseUrl), this.requestInit({
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
    const response = await fetch(
      new URL(`/v1/executions/${encodeURIComponent(executionId)}`, this.baseUrl),
      this.requestInit({ method: 'GET', ...(signal ? { signal } : {}) })
    );
    if (response.status === 404) return undefined;
    return await this.readJson<ExecutionSnapshot>(response);
  }

  async cancel(executionId: string, signal?: AbortSignal): Promise<boolean> {
    const response = await fetch(
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
    const response = await fetch(
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
    const response = await fetch(
      new URL(`/v1/artifacts/${encodeURIComponent(artifactId)}`, this.baseUrl),
      this.requestInit({ method: 'GET', ...(signal ? { signal } : {}) })
    );
    if (response.status === 404) return undefined;
    return await this.readJson<ArtifactSnapshot>(response);
  }

  async *streamEvents(
    executionId: string,
    afterSequence = 0,
    signal?: AbortSignal
  ): AsyncGenerator<ExecutionEvent> {
    const url = new URL(`/v1/executions/${encodeURIComponent(executionId)}/events`, this.baseUrl);
    url.searchParams.set('after', afterSequence.toString());
    const response = await fetch(url, this.requestInit({
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
          const data = block
            .split('\n')
            .filter(line => line.startsWith('data:'))
            .map(line => line.slice(5).trimStart())
            .join('\n');
          if (data) yield JSON.parse(data) as ExecutionEvent;
          boundary = buffer.indexOf('\n\n');
        }
      }
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

  private async createError(response: Response): Promise<Error> {
    const detail = await response.text();
    return new Error(`iRoute request failed with HTTP ${response.status}${detail ? `: ${detail}` : ''}`);
  }
}
