import type { CommunityApi } from './api';
export type Api = Pick<CommunityApi, 'user'|'profile'|'subscribe'|'restoreSession'|'sendEmailCode'|'verifyEmailCode'|'signOut'|'getProfile'|'saveProfile'|'getFeed'|'getClip'|'getComments'|'setLiked'|'addComment'|'deleteComment'|'reportClip'|'getBlockedUsers'|'setBlocked'|'deleteClip'|'getPlaybackUrl'|'getPosterUrl'|'uploadClip'>;
export const messageFor = (error:unknown) => error instanceof Error ? error.message : 'Something went wrong. Please try again.';
export const timeLabel = (value:number) => `${Math.floor(value/60)}:${Math.floor(value%60).toString().padStart(2,'0')}`;
export const sizeLabel = (value:number) => `${(value/1024/1024).toFixed(1)} MB`;
