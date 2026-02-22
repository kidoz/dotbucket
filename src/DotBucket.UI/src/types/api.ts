export interface Bucket {
  name: string;
  createdAt: string;
}

export interface StorageObject {
  bucketName: string;
  objectKey: string;
  size: number;
  contentType: string;
  eTag: string;
  lastModified: string;
  versionId: string;
  isLatest: boolean;
  isDeleteMarker: boolean;
  encryption?: string;
  lockStatus: {
    retentionMode?: string;
    retainUntilDate?: string;
    legalHold: boolean;
  };
  metadata: Record<string, string>;
}

export interface NotificationConfiguration {
  id: string;
  webhookUrl: string;
  events: string[];
  filterPrefix?: string;
}

export interface ClusterStatus {
  enabled: boolean;
  selfNodeId?: string;
  leaderNodeId?: string;
  replicationFactor?: number;
  writeQuorum?: number;
  readQuorum?: number;
  nodes?: ClusterNodeStatus[];
}

export interface ClusterNodeStatus {
  nodeId: string;
  address: string;
  isSelf: boolean;
  isLeader: boolean;
  status: string;
  lastSeen?: string;
}

export interface IamUser {
  userName: string;
  status: string;
  createdAt: string;
  updatedAt?: string;
}

export interface IamAccessKey {
  accessKey: string;
  userName: string;
  status: string;
  createdAt: string;
  secretKey?: string; // Only present at creation
}

export interface IamPolicy {
  policyName: string;
  policyJson: string;
  isBuiltin: boolean;
  createdAt: string;
}

export interface IamGroup {
  groupName: string;
  status: string;
  createdAt: string;
}