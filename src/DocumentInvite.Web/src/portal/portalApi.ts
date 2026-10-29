import { apiBaseUrl } from '../lib/api';
import type { PagedResponse } from '../lib/paging';

export type ItemKind = 'FileUpload' | 'FormField';
export type FieldType = 'Text' | 'Number' | 'Date' | 'YesNo';
export type ItemStatus = 'Outstanding' | 'Submitted' | 'Approved' | 'Rejected';

export interface PortalFile {
  id: string;
  fileName: string;
  sizeBytes: number;
  uploadedAt: string | null;
}

export interface PortalItem {
  id: string;
  name: string;
  kind: ItemKind;
  instructions: string;
  allowMultiple: boolean;
  fieldType: FieldType | null;
  isRequired: boolean;
  status: ItemStatus;
  rejectionReason: string | null;
  fieldValue: string | null;
  note: string | null;
  files: PortalFile[];
}

export interface PortalInvite {
  requestName: string;
  partyFirstName: string;
  dueDate: string;
  status: string;
  privacyAccepted: boolean;
  itemCount: number;
  completedItemCount: number;
  items: PortalItem[];
}

/** A problem+json answer from the portal API. `code` is the machine-readable reason. */
export class PortalApiError extends Error {
  readonly status: number;
  readonly code: string | null;
  readonly fieldErrors: Record<string, string[]>;

  constructor(status: number, message: string, code: string | null = null, fieldErrors: Record<string, string[]> = {}) {
    super(message);
    this.name = 'PortalApiError';
    this.status = status;
    this.code = code;
    this.fieldErrors = fieldErrors;
  }

  get messages(): string[] {
    const fromFields = Object.values(this.fieldErrors).flat();
    return fromFields.length > 0 ? fromFields : [this.message];
  }
}

async function toError(response: Response): Promise<PortalApiError> {
  try {
    const problem = (await response.json()) as {
      title?: string;
      detail?: string;
      code?: string;
      errors?: Record<string, string[]>;
    };
    return new PortalApiError(
      response.status,
      problem.detail ?? problem.title ?? 'Something went wrong. Please try again.',
      problem.code ?? null,
      problem.errors ?? {},
    );
  } catch {
    return new PortalApiError(response.status, 'Something went wrong. Please try again.');
  }
}

// ---- the portal session ----
// The session token lives in sessionStorage for this tab only and is tied to the link token it was
// created for. It is never logged or sent anywhere except the portal API.

const SESSION_KEY = 'di.portalSession';

interface StoredSession {
  linkToken: string;
  sessionToken: string;
}

let memorySession: StoredSession | null = null;

function readStored(): StoredSession | null {
  if (memorySession) return memorySession;
  try {
    const raw = sessionStorage.getItem(SESSION_KEY);
    return raw ? (JSON.parse(raw) as StoredSession) : null;
  } catch {
    return null;
  }
}

function storeSession(session: StoredSession | null): void {
  memorySession = session;
  try {
    if (session) sessionStorage.setItem(SESSION_KEY, JSON.stringify(session));
    else sessionStorage.removeItem(SESSION_KEY);
  } catch {
    // Storage can be unavailable (private windows); the session then lasts until reload.
  }
}

/** True when this tab already holds a session for the given link. */
export function hasSessionFor(linkToken: string): boolean {
  return readStored()?.linkToken === linkToken;
}

export function clearPortalSession(): void {
  storeSession(null);
}

const exchangesInFlight = new Map<string, Promise<void>>();

/**
 * Exchanges the link token (and the password, if one is set) for a portal session. Identical calls made
 * at the same time (React strict mode runs effects twice in development) share one request, so one
 * page load is one audited open.
 */
export function startSession(linkToken: string, password?: string): Promise<void> {
  const key = `${linkToken}
${password ?? ''}`;
  let pending = exchangesInFlight.get(key);
  if (!pending) {
    pending = exchange(linkToken, password).finally(() => exchangesInFlight.delete(key));
    exchangesInFlight.set(key, pending);
  }
  return pending;
}

async function exchange(linkToken: string, password?: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/portal/session`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token: linkToken, password: password || null }),
  });
  if (!response.ok) throw await toError(response);
  const body = (await response.json()) as { sessionToken: string };
  storeSession({ linkToken, sessionToken: body.sessionToken });
}

async function request(path: string, method: string, body?: unknown): Promise<Response> {
  const session = readStored();
  const headers: Record<string, string> = {};
  if (session) headers.Authorization = `Bearer ${session.sessionToken}`;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(apiBaseUrl + path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (response.status === 401) {
    clearPortalSession();
    throw new PortalApiError(401, 'Your session has ended. Please reopen your link.', 'session_ended');
  }
  if (!response.ok) throw await toError(response);
  return response;
}

async function call<T>(path: string, method = 'GET', body?: unknown): Promise<T> {
  const response = await request(path, method, body);
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

export const getInvite = () => call<PortalInvite>('/api/portal/invite');

export const acceptPrivacy = () => call<void>('/api/portal/privacy', 'POST');

export const saveField = (itemId: string, value: string) =>
  call<PortalItem>(`/api/portal/items/${itemId}/field`, 'PUT', { value });

export const saveNote = (itemId: string, note: string) =>
  call<PortalItem>(`/api/portal/items/${itemId}/note`, 'PUT', { note });

// ---- uploads ----

export const MAX_FILE_BYTES = 25 * 1024 * 1024;

const TYPES_BY_EXTENSION: Record<string, string> = {
  pdf: 'application/pdf',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  png: 'image/png',
  docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  xlsx: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  csv: 'text/csv',
};

/** The file input `accept` value: the allowed extensions. */
export const ACCEPT = Object.keys(TYPES_BY_EXTENSION)
  .map((ext) => `.${ext}`)
  .join(',');

export const ALLOWED_LABEL = 'PDF, JPG, PNG, DOCX, XLSX or CSV, up to 25 MB';

/** The content type for a file, from its extension (browsers report CSV and Office files unreliably). */
export function contentTypeFor(fileName: string): string | null {
  const ext = fileName.split('.').pop()?.toLowerCase() ?? '';
  return fileName.includes('.') ? (TYPES_BY_EXTENSION[ext] ?? null) : null;
}

/** Why a file cannot be uploaded, or null when it can. Mirrors the server's checks so the party gets quick feedback. */
export function fileProblem(file: { name: string; size: number }): string | null {
  if (contentTypeFor(file.name) === null) return `${file.name}: only ${ALLOWED_LABEL} can be uploaded.`;
  if (file.size === 0) return `${file.name}: the file is empty.`;
  if (file.size > MAX_FILE_BYTES) return `${file.name}: files can be at most 25 MB.`;
  return null;
}

interface UploadSlot {
  fileId: string;
  uploadUrl: string;
  requiredHeaders: Record<string, string>;
}

function putBlob(
  slot: UploadSlot,
  file: File,
  contentType: string,
  onProgress: (fraction: number) => void,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('PUT', slot.uploadUrl);
    for (const [name, value] of Object.entries(slot.requiredHeaders)) xhr.setRequestHeader(name, value);
    if (!slot.requiredHeaders['Content-Type']) xhr.setRequestHeader('Content-Type', contentType);
    xhr.upload.onprogress = (event) => {
      if (event.lengthComputable) onProgress(event.loaded / event.total);
    };
    xhr.onload = () =>
      xhr.status >= 200 && xhr.status < 300
        ? resolve()
        : reject(new PortalApiError(xhr.status, 'The upload did not finish. Please try again.', 'upload_failed'));
    xhr.onerror = () => reject(new PortalApiError(0, 'The upload did not finish. Check your connection and try again.', 'upload_failed'));
    xhr.send(file);
  });
}

export interface UploadProgress {
  /** 0 to 1 while the bytes are sent. */
  fraction: number;
  /** True once the bytes are sent and the server is checking the file. */
  processing: boolean;
}

export type UploadFunction = (
  itemId: string,
  file: File,
  onProgress: (progress: UploadProgress) => void,
) => Promise<PortalFile>;

/** Requests an upload slot, sends the file straight to blob storage, then asks the API to complete it. */
export const uploadFile: UploadFunction = async (itemId, file, onProgress) => {
  const contentType = contentTypeFor(file.name);
  if (!contentType) throw new PortalApiError(400, fileProblem(file) ?? 'This file type is not allowed.', 'unsupported_type');

  const slot = await call<UploadSlot>(`/api/portal/items/${itemId}/upload-slot`, 'POST', {
    fileName: file.name,
    contentType,
    sizeBytes: file.size,
  });
  await putBlob(slot, file, contentType, (fraction) => onProgress({ fraction, processing: false }));
  onProgress({ fraction: 1, processing: true });
  return call<PortalFile>(`/api/portal/items/${itemId}/complete`, 'POST', { fileId: slot.fileId });
};

// ---- messages ----

export interface PortalMessage {
  id: string;
  authorType: 'Team' | 'Party' | 'System';
  body: string;
  createdAt: string;
}

/** A page of the thread, newest first. Internal notes are never part of it. */
export const listMessages = (page: number, pageSize: number) =>
  call<PagedResponse<PortalMessage>>(`/api/portal/messages?page=${page}&pageSize=${pageSize}`);

export const postMessage = (body: string) => call<PortalMessage>('/api/portal/messages', 'POST', { body });
