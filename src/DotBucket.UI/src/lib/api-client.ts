import type {
  Bucket,
  StorageObject,
  NotificationConfiguration,
  ClusterStatus,
  IamUser,
  IamAccessKey,
  IamPolicy,
  IamGroup,
} from '../types/api';
import { getToken } from './auth';

const API_BASE = '/admin';

async function apiFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const token = getToken();
  const headers = new Headers(init?.headers);
  if (token) {
    headers.set('Authorization', `Bearer ${token}`);
  }

  const res = await fetch(input, { ...init, headers });

  if (res.status === 401) {
    window.dispatchEvent(new Event('dotbucket:unauthorized'));
  }

  return res;
}

export const apiClient = {
  getBuckets: async (): Promise<Bucket[]> => {
    const res = await apiFetch(`${API_BASE}/buckets`);
    if (!res.ok) throw new Error('Failed to fetch buckets');
    return res.json();
  },

  createBucket: async (name: string): Promise<Bucket> => {
    const res = await apiFetch(`${API_BASE}/buckets`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    });
    if (!res.ok) throw new Error('Failed to create bucket');
    return res.json();
  },

  deleteBucket: async (bucketName: string): Promise<void> => {
    const res = await apiFetch(`${API_BASE}/buckets/${bucketName}`, {
      method: 'DELETE',
    });
    if (!res.ok) throw new Error('Failed to delete bucket');
  },

  getObjects: async (
    bucketName: string,
    versions: boolean = false,
    page: number = 1,
    pageSize: number = 50,
    prefix?: string,
  ): Promise<{ objects: StorageObject[]; totalCount: number }> => {
    const params = new URLSearchParams({
      versions: String(versions),
      page: String(page),
      pageSize: String(pageSize),
    });
    if (prefix) params.set('prefix', prefix);
    const res = await apiFetch(`${API_BASE}/buckets/${bucketName}/objects?${params}`);
    if (!res.ok) throw new Error('Failed to fetch objects');
    return res.json();
  },

  getVersioning: async (bucketName: string): Promise<string> => {
    const res = await apiFetch(`${API_BASE}/buckets/${bucketName}/versioning`);
    if (!res.ok) throw new Error('Failed to get versioning');
    const data = await res.json();
    return data.status;
  },

  setVersioning: async (bucketName: string, status: string): Promise<void> => {
    const res = await apiFetch(`${API_BASE}/buckets/${bucketName}/versioning`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ status }),
    });
    if (!res.ok) throw new Error('Failed to set versioning');
  },

  getNotifications: async (bucketName: string): Promise<NotificationConfiguration[]> => {
    const res = await apiFetch(`${API_BASE}/buckets/${bucketName}/notifications`);
    if (!res.ok) throw new Error('Failed to get notifications');
    return res.json();
  },

  setNotifications: async (
    bucketName: string,
    notifications: NotificationConfiguration[],
  ): Promise<void> => {
    const res = await apiFetch(`${API_BASE}/buckets/${bucketName}/notifications`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(notifications),
    });
    if (!res.ok) throw new Error('Failed to set notifications');
  },

  uploadObject: async (
    bucketName: string,
    file: File,
    onProgress?: (loaded: number, total: number) => void,
  ): Promise<StorageObject> => {
    const formData = new FormData();
    formData.append('file', file);

    if (onProgress) {
      return new Promise<StorageObject>((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        xhr.open('POST', `${API_BASE}/buckets/${bucketName}/upload`);
        const token = getToken();
        if (token) xhr.setRequestHeader('Authorization', `Bearer ${token}`);
        xhr.upload.onprogress = (e) => {
          if (e.lengthComputable) onProgress(e.loaded, e.total);
        };
        xhr.onload = () => {
          if (xhr.status >= 200 && xhr.status < 300) {
            resolve(JSON.parse(xhr.responseText));
          } else if (xhr.status === 401) {
            window.dispatchEvent(new Event('dotbucket:unauthorized'));
            reject(new Error('Unauthorized'));
          } else {
            reject(new Error('Failed to upload file'));
          }
        };
        xhr.onerror = () => reject(new Error('Failed to upload file'));
        xhr.send(formData);
      });
    }

    const res = await apiFetch(`${API_BASE}/buckets/${bucketName}/upload`, {
      method: 'POST',
      body: formData,
    });
    if (!res.ok) throw new Error('Failed to upload file');
    return res.json();
  },

  deleteObject: async (bucketName: string, objectKey: string, versionId?: string): Promise<void> => {
    let url = `${API_BASE}/buckets/${bucketName}/objects/${encodeURIComponent(objectKey)}`;
    if (versionId) url += `?versionId=${versionId}`;
    const res = await apiFetch(url, {
      method: 'DELETE',
    });
    if (!res.ok) throw new Error('Failed to delete object');
  },

  downloadObject: async (bucketName: string, objectKey: string): Promise<Blob> => {
    const res = await apiFetch(
      `${API_BASE}/buckets/${bucketName}/download/${encodeURIComponent(objectKey)}`,
    );
    if (!res.ok) throw new Error('Failed to download object');
    return res.blob();
  },

  getClusterStatus: async (): Promise<ClusterStatus> => {
    const res = await apiFetch(`${API_BASE}/cluster`);
    if (!res.ok) throw new Error('Failed to fetch cluster status');
    return res.json();
  },

  // IAM Methods
  getUsers: async (): Promise<IamUser[]> => {
    const res = await apiFetch(`${API_BASE}/iam/users`);
    if (!res.ok) throw new Error('Failed to fetch users');
    return res.json();
  },

  createUser: async (userName: string): Promise<IamUser> => {
    const res = await apiFetch(`${API_BASE}/iam/users`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userName }),
    });
    if (!res.ok) throw new Error('Failed to create user');
    return res.json();
  },

  deleteUser: async (userName: string): Promise<void> => {
    const res = await apiFetch(`${API_BASE}/iam/users/${userName}`, {
      method: 'DELETE',
    });
    if (!res.ok) throw new Error('Failed to delete user');
  },

  getAccessKeys: async (userName: string): Promise<IamAccessKey[]> => {
    const res = await apiFetch(`${API_BASE}/iam/users/${userName}/access-keys`);
    if (!res.ok) throw new Error('Failed to fetch access keys');
    return res.json();
  },

  createAccessKey: async (userName: string): Promise<IamAccessKey> => {
    const res = await apiFetch(`${API_BASE}/iam/users/${userName}/access-keys`, {
      method: 'POST',
    });
    if (!res.ok) throw new Error('Failed to create access key');
    return res.json();
  },

  deleteAccessKey: async (accessKey: string): Promise<void> => {
    const res = await apiFetch(`${API_BASE}/iam/access-keys/${accessKey}`, {
      method: 'DELETE',
    });
    if (!res.ok) throw new Error('Failed to delete access key');
  },

  getPolicies: async (): Promise<IamPolicy[]> => {
    const res = await apiFetch(`${API_BASE}/iam/policies`);
    if (!res.ok) throw new Error('Failed to fetch policies');
    return res.json();
  },

  getGroups: async (): Promise<IamGroup[]> => {
    const res = await apiFetch(`${API_BASE}/iam/groups`);
    if (!res.ok) throw new Error('Failed to fetch groups');
    return res.json();
  },
};
