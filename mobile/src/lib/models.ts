export interface CommunityConfig { projectUrl: string; publishableKey: string }
export interface CommunityUser { id: string; email: string }
export interface CommunityProfile { id: string; username: string | null; displayName: string; bio: string }
export interface CommunityClip {
  id: string; ownerId: string; username: string; displayName: string; title: string; description: string;
  durationSeconds: number; byteSize: number; createdAt: string; likeCount: number; commentCount: number;
  likedByMe: boolean; status: 'uploading' | 'published' | 'deleting';
}
export interface CommunityComment {
  id: string; clipId: string; ownerId: string; username: string; displayName: string; body: string; createdAt: string;
}
export interface CommunityCursor { createdAt: string; id: string }
export interface CommunityPage<T> { items: T[]; nextCursor: CommunityCursor | null }
export interface CommunityBlockedUser { userId: string; username: string; displayName: string }
export interface CommunityUpload {
  video: Blob; poster: Blob; title: string; description: string; durationSeconds: number;
}
export interface CommunityUploadProgress { completedBytes: number; totalBytes: number; status: string }
export interface StorageLike {
  getItem(key: string): string | null | Promise<string | null>;
  setItem(key: string, value: string): void | Promise<void>;
  removeItem(key: string): void | Promise<void>;
}

export const mediaLimits = Object.freeze({
  videoBytes: 16 * 1024 * 1024, posterBytes: 256 * 1024, durationSeconds: 60,
  feedPageSize: 12, commentsPageSize: 30, titleLength: 120, descriptionLength: 500, commentLength: 500,
});

export class CommunityError extends Error {
  constructor(public readonly code: string, message: string) { super(message); this.name = 'CommunityError'; }
}

export const unavailable = () => new CommunityError('unavailable', 'Media is unavailable right now. Please try again.');
export const cancelled = () => new DOMException('The operation was canceled.', 'AbortError');
export const isCancelled = (error: unknown) => error instanceof Error && error.name === 'AbortError';
