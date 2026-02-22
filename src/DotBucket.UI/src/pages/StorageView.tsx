import { useEffect, useState, useCallback } from 'react';
import type { Bucket, StorageObject, NotificationConfiguration } from '../types/api';
import { apiClient } from '../lib/api-client';
import { validateBucketName } from '../lib/bucket-validation';
import { DropZone } from '../components/DropZone';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Progress } from '@/components/ui/progress';
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog';
import { Label } from '@/components/ui/label';
import {
  Folder,
  File as FileIcon,
  Trash2,
  Plus,
  Loader2,
  AlertCircle,
  History,
  ShieldAlert,
  ShieldCheck,
  Bell,
  Download,
  ChevronLeft,
  ChevronRight,
  Search,
  Info
} from 'lucide-react';

interface UploadProgress {
  fileName: string;
  loaded: number;
  total: number;
}

interface DeleteConfirmation {
  type: 'object' | 'bucket';
  title: string;
  description: string;
  onConfirm: () => void;
}

export function StorageView() {
  const [buckets, setBuckets] = useState<Bucket[]>([]);
  const [selectedBucket, setSelectedBucket] = useState<string | null>(null);
  const [objects, setObjects] = useState<StorageObject[]>([]);
  const [totalObjectCount, setTotalObjectCount] = useState(0);
  const [newBucketName, setNewBucketName] = useState('');
  const [bucketNameError, setBucketNameError] = useState('');
  const [loading, setLoading] = useState(true);
  const [uploading, setUploading] = useState(false);
  const [uploadProgress, setUploadProgress] = useState<UploadProgress[]>([]);
  const [error, setError] = useState('');
  const [showVersions, setShowVersions] = useState(false);
  const [versioningStatus, setVersioningStatus] = useState('Off');
  const [notifications, setNotifications] = useState<NotificationConfiguration[]>([]);
  const [isWebhookDialogOpen, setIsWebhookDialogOpen] = useState(false);
  const [newWebhookUrl, setNewWebhookUrl] = useState('');
  const [newWebhookPrefix, setNewWebhookPrefix] = useState('');
  const [currentPage, setCurrentPage] = useState(1);
  const [pageSize] = useState(50);
  const [searchPrefix, setSearchPrefix] = useState('');
  const [deleteConfirm, setDeleteConfirm] = useState<DeleteConfirmation | null>(null);
  const [selectedObject, setSelectedObject] = useState<StorageObject | null>(null);

  const loadBuckets = useCallback(async () => {
    try {
      setLoading(true);
      const data = await apiClient.getBuckets();
      setBuckets(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load buckets');
    } finally {
      setLoading(false);
    }
  }, []);

  const loadObjects = useCallback(
    async (bucketName: string, versions: boolean, page: number, prefix?: string) => {
      try {
        setLoading(true);
        const data = await apiClient.getObjects(
          bucketName,
          versions,
          page,
          pageSize,
          prefix || undefined,
        );
        setObjects(data.objects);
        setTotalObjectCount(data.totalCount);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to load objects');
      } finally {
        setLoading(false);
      }
    },
    [pageSize],
  );

  const loadVersioning = useCallback(async (bucketName: string) => {
    try {
      const status = await apiClient.getVersioning(bucketName);
      setVersioningStatus(status);
    } catch (err) {
      console.error('Failed to load versioning status', err);
    }
  }, []);

  const loadNotifications = useCallback(async (bucketName: string) => {
    try {
      const data = await apiClient.getNotifications(bucketName);
      setNotifications(data);
    } catch (err) {
      console.error('Failed to load notifications', err);
    }
  }, []);

  useEffect(() => {
    loadBuckets();
  }, [loadBuckets]);

  useEffect(() => {
    if (selectedBucket) {
      loadObjects(selectedBucket, showVersions, currentPage, searchPrefix);
      loadVersioning(selectedBucket);
      loadNotifications(selectedBucket);
    } else {
      setObjects([]);
      setTotalObjectCount(0);
      setVersioningStatus('Off');
      setNotifications([]);
    }
  }, [selectedBucket, showVersions, currentPage, searchPrefix, loadObjects, loadVersioning, loadNotifications]);

  const handleSetVersioning = async (status: string) => {
    if (!selectedBucket) return;
    try {
      await apiClient.setVersioning(selectedBucket, status);
      setVersioningStatus(status);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to set versioning');
    }
  };

  const handleAddWebhook = async () => {
    if (!selectedBucket || !newWebhookUrl.trim()) return;

    const newConfig: NotificationConfiguration = {
      id: Math.random().toString(36).substring(2, 15),
      webhookUrl: newWebhookUrl,
      events: ['s3:ObjectCreated:*', 's3:ObjectRemoved:*'],
      filterPrefix: newWebhookPrefix || undefined,
    };

    const updated = [...notifications, newConfig];
    try {
      await apiClient.setNotifications(selectedBucket, updated);
      setNotifications(updated);
      setNewWebhookUrl('');
      setNewWebhookPrefix('');
      setIsWebhookDialogOpen(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save webhook');
    }
  };

  const handleRemoveWebhook = async (id: string) => {
    if (!selectedBucket) return;
    const updated = notifications.filter((n) => n.id !== id);
    try {
      await apiClient.setNotifications(selectedBucket, updated);
      setNotifications(updated);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to remove webhook');
    }
  };

  const handleBucketNameChange = (name: string) => {
    setNewBucketName(name);
    if (name.trim()) {
      const result = validateBucketName(name);
      setBucketNameError(result.valid ? '' : result.error || '');
    } else {
      setBucketNameError('');
    }
  };

  const handleCreateBucket = async () => {
    if (!newBucketName.trim()) return;
    const result = validateBucketName(newBucketName);
    if (!result.valid) {
      setBucketNameError(result.error || 'Invalid bucket name.');
      return;
    }
    try {
      await apiClient.createBucket(newBucketName);
      setNewBucketName('');
      setBucketNameError('');
      await loadBuckets();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create bucket');
    }
  };

  const handleDeleteBucket = (bucketName: string) => {
    setDeleteConfirm({
      type: 'bucket',
      title: `Delete bucket "${bucketName}"?`,
      description:
        'This will permanently delete the bucket and all associated configuration. The bucket must be empty.',
      onConfirm: async () => {
        try {
          await apiClient.deleteBucket(bucketName);
          if (selectedBucket === bucketName) {
            setSelectedBucket(null);
          }
          await loadBuckets();
        } catch (err) {
          setError(err instanceof Error ? err.message : 'Failed to delete bucket');
        }
        setDeleteConfirm(null);
      },
    });
  };

  const handleUploadFiles = useCallback(
    async (files: File[]) => {
      if (!selectedBucket || files.length === 0) return;

      setUploading(true);
      const progressMap = files.map((f) => ({
        fileName: f.name,
        loaded: 0,
        total: f.size,
      }));
      setUploadProgress(progressMap);

      for (let i = 0; i < files.length; i++) {
        try {
          await apiClient.uploadObject(selectedBucket, files[i], (loaded, total) => {
            setUploadProgress((prev) =>
              prev.map((p, idx) => (idx === i ? { ...p, loaded, total } : p)),
            );
          });
        } catch (err) {
          setError(err instanceof Error ? err.message : `Failed to upload ${files[i].name}`);
        }
      }

      setUploading(false);
      setUploadProgress([]);
      await loadObjects(selectedBucket, showVersions, currentPage, searchPrefix);
    },
    [selectedBucket, showVersions, currentPage, searchPrefix, loadObjects],
  );

  const handleDeleteObject = (key: string, versionId?: string) => {
    const msg = versionId
      ? `Permanently delete version ${versionId} of ${key}?`
      : `Delete ${key}? (This will create a Delete Marker if versioning is Enabled)`;

    setDeleteConfirm({
      type: 'object',
      title: 'Delete Object',
      description: msg,
      onConfirm: async () => {
        if (!selectedBucket) return;
        try {
          await apiClient.deleteObject(selectedBucket, key, versionId);
          await loadObjects(selectedBucket, showVersions, currentPage, searchPrefix);
        } catch (err) {
          setError(err instanceof Error ? err.message : 'Failed to delete object');
        }
        setDeleteConfirm(null);
      },
    });
  };

  const handleDownload = async (objectKey: string) => {
    if (!selectedBucket) return;
    try {
      const blob = await apiClient.downloadObject(selectedBucket, objectKey);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = objectKey.split('/').pop() || objectKey;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to download object');
    }
  };

  const totalPages = Math.max(1, Math.ceil(totalObjectCount / pageSize));

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h2 className="text-2xl font-bold tracking-tight">Storage Browser</h2>
      </div>

      {error && (
        <div
          className="relative flex items-center justify-between rounded border border-red-400 bg-red-100 px-4 py-3 text-red-700"
          role="alert"
        >
          <div className="flex items-center">
            <AlertCircle className="mr-2 h-5 w-5" />
            <span className="block sm:inline">{error}</span>
          </div>
          <button className="text-red-700 hover:text-red-900" onClick={() => setError('')}>
            <span className="text-xl">&times;</span>
          </button>
        </div>
      )}

      <div className="grid grid-cols-1 gap-6 md:grid-cols-3 lg:grid-cols-4">
        {/* Buckets List */}
        <Card className="col-span-1 shadow-sm">
          <CardHeader>
            <CardTitle>Buckets</CardTitle>
            <CardDescription>Manage your storage buckets.</CardDescription>
          </CardHeader>
          <CardContent>
            <div className="mb-2 flex space-x-2">
              <Input
                placeholder="New bucket name"
                value={newBucketName}
                onChange={(e) => handleBucketNameChange(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && handleCreateBucket()}
              />
              <Button
                onClick={handleCreateBucket}
                size="icon"
                variant="secondary"
                title="Create Bucket"
              >
                <Plus className="h-4 w-4" />
              </Button>
            </div>
            {bucketNameError && (
              <p className="mb-3 text-xs text-red-600">{bucketNameError}</p>
            )}

            <div className="max-h-[60vh] space-y-2 overflow-y-auto pr-1">
              {buckets.length === 0 && !loading && (
                <p className="py-4 text-center text-sm italic text-gray-500">No buckets found.</p>
              )}
              {buckets.map((bucket) => (
                <div
                  key={bucket.name}
                  className={`group relative flex cursor-pointer items-center rounded-md p-3 transition-all hover:shadow-sm ${
                    selectedBucket === bucket.name
                      ? 'border-l-4 border-slate-900 bg-slate-100 shadow-sm'
                      : 'border border-slate-200 bg-white hover:bg-slate-50'
                  }`}
                >
                  <div
                    className="flex flex-1 items-center overflow-hidden"
                    onClick={() => {
                      setSelectedBucket(bucket.name);
                      setCurrentPage(1);
                      setSearchPrefix('');
                    }}
                  >
                    <Folder
                      className={`mr-3 h-5 w-5 ${selectedBucket === bucket.name ? 'text-indigo-600' : 'text-slate-400'}`}
                    />
                    <div className="flex-1 overflow-hidden">
                      <p className="truncate text-sm font-semibold">{bucket.name}</p>
                      <p className="font-mono text-xs text-slate-500">
                        {new Date(bucket.createdAt).toLocaleDateString()}
                      </p>
                    </div>
                  </div>
                  <Button
                    variant="ghost"
                    size="icon"
                    onClick={(e) => {
                      e.stopPropagation();
                      handleDeleteBucket(bucket.name);
                    }}
                    className="h-7 w-7 text-red-500 opacity-0 transition-opacity hover:bg-red-50 hover:text-red-700 group-hover:opacity-100"
                  >
                    <Trash2 className="h-3.5 w-3.5" />
                  </Button>
                </div>
              ))}
            </div>
          </CardContent>
        </Card>

        {/* Objects List */}
        <Card className="col-span-1 md:col-span-2 lg:col-span-3 shadow-sm">
          <CardHeader className="flex flex-col space-y-4">
            <div className="flex flex-row items-center justify-between">
              <div>
                <CardTitle>
                  {selectedBucket ? `Objects in ${selectedBucket}` : 'Select a bucket'}
                </CardTitle>
                <CardDescription>View and manage files stored in this bucket.</CardDescription>
              </div>
            </div>

            {selectedBucket && (
              <div className="flex flex-col space-y-4 border-t pt-4">
                <div className="flex items-center justify-between">
                  <div className="flex items-center space-x-4">
                    <div className="flex items-center space-x-2 text-sm text-slate-600">
                      <History className="h-4 w-4" />
                      <span>Versioning:</span>
                      <select
                        value={versioningStatus}
                        onChange={(e) => handleSetVersioning(e.target.value)}
                        className="rounded border bg-transparent p-1 text-xs outline-none focus:ring-1 focus:ring-indigo-500"
                      >
                        <option value="Off">Off</option>
                        <option value="Enabled">Enabled</option>
                        <option value="Suspended">Suspended</option>
                      </select>
                    </div>
                    <label className="flex cursor-pointer items-center space-x-2 text-sm text-slate-600">
                      <input
                        type="checkbox"
                        checked={showVersions}
                        onChange={(e) => setShowVersions(e.target.checked)}
                        className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
                      />
                      <span>Show Versions</span>
                    </label>
                  </div>

                  <Dialog open={isWebhookDialogOpen} onOpenChange={setIsWebhookDialogOpen}>
                    <DialogTrigger asChild>
                      <Button variant="outline" size="sm" className="h-8">
                        <Bell className="mr-2 h-4 w-4" />
                        Webhooks ({notifications.length})
                      </Button>
                    </DialogTrigger>
                    <DialogContent className="sm:max-w-[425px]">
                      <DialogHeader>
                        <DialogTitle>Bucket Webhooks</DialogTitle>
                        <DialogDescription>
                          Configure HTTP POST notifications for events in this bucket.
                        </DialogDescription>
                      </DialogHeader>
                      <div className="grid gap-4 py-4">
                        <div className="space-y-2">
                          <Label htmlFor="url">Webhook URL</Label>
                          <Input
                            id="url"
                            placeholder="https://your-api.com/webhooks/s3"
                            value={newWebhookUrl}
                            onChange={(e) => setNewWebhookUrl(e.target.value)}
                          />
                        </div>
                        <div className="space-y-2">
                          <Label htmlFor="prefix">Prefix Filter (Optional)</Label>
                          <Input
                            id="prefix"
                            placeholder="images/"
                            value={newWebhookPrefix}
                            onChange={(e) => setNewWebhookPrefix(e.target.value)}
                          />
                        </div>
                        <Button onClick={handleAddWebhook} className="w-full">
                          Add Webhook
                        </Button>

                        <div className="border-t pt-4">
                          <Label className="mb-2 block">Active Webhooks</Label>
                          <div className="max-h-[200px] space-y-2 overflow-y-auto">
                            {notifications.length === 0 && (
                              <p className="text-xs italic text-slate-500">
                                No webhooks configured.
                              </p>
                            )}
                            {notifications.map((n) => (
                              <div
                                key={n.id}
                                className="flex items-center justify-between rounded border bg-slate-50 p-2 text-xs"
                              >
                                <div className="mr-2 flex-1 overflow-hidden">
                                  <p className="truncate font-semibold">{n.webhookUrl}</p>
                                  <p className="italic text-slate-500">
                                    {n.filterPrefix ? `Prefix: ${n.filterPrefix}` : 'All objects'}
                                  </p>
                                </div>
                                <Button
                                  variant="ghost"
                                  size="icon"
                                  onClick={() => handleRemoveWebhook(n.id)}
                                  className="h-6 w-6 text-red-500"
                                >
                                  <Trash2 className="h-3 w-3" />
                                </Button>
                              </div>
                            ))}
                          </div>
                        </div>
                      </div>
                    </DialogContent>
                  </Dialog>
                </div>

                {/* Prefix search */}
                <div className="relative">
                  <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                  <Input
                    placeholder="Search by prefix..."
                    value={searchPrefix}
                    onChange={(e) => setSearchPrefix(e.target.value)}
                    className="pl-9"
                  />
                </div>

                {/* Drop Zone */}
                <DropZone onFiles={handleUploadFiles} disabled={uploading} />

                {/* Upload progress */}
                {uploadProgress.length > 0 && (
                  <div className="space-y-2">
                    {uploadProgress.map((p, idx) => (
                      <div key={idx} className="space-y-1">
                        <div className="flex items-center justify-between text-xs text-slate-600">
                          <span className="truncate">{p.fileName}</span>
                          <span>
                            {p.total > 0 ? Math.round((p.loaded / p.total) * 100) : 0}%
                          </span>
                        </div>
                        <Progress
                          value={p.total > 0 ? (p.loaded / p.total) * 100 : 0}
                          className="h-1.5"
                        />
                      </div>
                    ))}
                  </div>
                )}
              </div>
            )}
          </CardHeader>
          <CardContent>
            {!selectedBucket ? (
              <div className="flex flex-col items-center justify-center py-24 text-slate-400">
                <Folder className="mb-4 h-16 w-16 opacity-10" />
                <p className="text-lg">Select a bucket to view and manage its data.</p>
              </div>
            ) : (
              <>
                <div className="overflow-hidden rounded-md border">
                  <Table>
                    <TableHeader className="bg-slate-50">
                      <TableRow>
                        <TableHead>Object Key</TableHead>
                        {showVersions && <TableHead>Version ID</TableHead>}
                        <TableHead className="text-right">Size</TableHead>
                        <TableHead>Last Modified</TableHead>
                        <TableHead className="w-[120px] text-center">Actions</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {objects.length === 0 && !loading && !uploading && (
                        <TableRow>
                          <TableCell
                            colSpan={showVersions ? 5 : 4}
                            className="py-16 text-center text-slate-400"
                          >
                            <FileIcon className="mx-auto mb-2 h-8 w-8 opacity-20" />
                            <p>This bucket is empty.</p>
                          </TableCell>
                        </TableRow>
                      )}
                      {objects.map((obj) => (
                        <TableRow
                          key={obj.objectKey + obj.versionId}
                          className={`transition-colors hover:bg-slate-50 ${obj.isDeleteMarker ? 'bg-slate-50 opacity-60' : ''}`}
                        >
                          <TableCell className="flex max-w-[300px] items-center truncate font-medium">
                            {obj.isDeleteMarker ? (
                              <ShieldAlert className="mr-2 h-4 w-4 flex-shrink-0 text-orange-500" />
                            ) : (
                              <FileIcon className="mr-2 h-4 w-4 flex-shrink-0 text-slate-400" />
                            )}
                            <span className={obj.isDeleteMarker ? 'italic text-slate-500' : ''}>
                              {obj.objectKey}
                              {obj.isDeleteMarker && (
                                <span className="ml-2 rounded bg-orange-100 px-1 text-[10px] text-orange-700">
                                  Delete Marker
                                </span>
                              )}
                              {!obj.isLatest && (
                                <span className="ml-2 rounded bg-slate-100 px-1 text-[10px] text-slate-600">
                                  Old Version
                                </span>
                              )}
                            </span>
                          </TableCell>
                          {showVersions && (
                            <TableCell
                              className="max-w-[80px] truncate font-mono text-[10px] text-slate-500"
                              title={obj.versionId}
                            >
                              {obj.versionId === 'null' ? '-' : obj.versionId.substring(0, 8)}
                            </TableCell>
                          )}
                          <TableCell className="text-right font-mono text-sm">
                            {obj.isDeleteMarker
                              ? '-'
                              : (obj.size / 1024).toLocaleString(undefined, {
                                  maximumFractionDigits: 1,
                                }) + ' KB'}
                          </TableCell>
                          <TableCell className="text-sm text-slate-600">
                            {new Date(obj.lastModified).toLocaleString()}
                          </TableCell>
                          <TableCell className="text-center">
                            <div className="flex items-center justify-center space-x-1">
                              <Button
                                variant="ghost"
                                size="icon"
                                onClick={() => setSelectedObject(obj)}
                                className="h-8 w-8 text-slate-500 hover:bg-slate-100 hover:text-slate-700"
                                title="Details"
                              >
                                <Info className="h-4 w-4" />
                              </Button>
                              {!obj.isDeleteMarker && (
                                <Button
                                  variant="ghost"
                                  size="icon"
                                  onClick={() => handleDownload(obj.objectKey)}
                                  className="h-8 w-8 text-slate-500 hover:bg-slate-100 hover:text-slate-700"
                                  title="Download"
                                >
                                  <Download className="h-4 w-4" />
                                </Button>
                              )}
                              <Button
                                variant="ghost"
                                size="icon"
                                onClick={() =>
                                  handleDeleteObject(
                                    obj.objectKey,
                                    showVersions ? obj.versionId : undefined,
                                  )
                                }
                                className="h-8 w-8 text-red-500 hover:bg-red-50 hover:text-red-700"
                                title="Delete"
                              >
                                <Trash2 className="h-4 w-4" />
                              </Button>
                            </div>
                          </TableCell>
                        </TableRow>
                      ))}
                      {uploading && (
                        <TableRow>
                          <TableCell
                            colSpan={showVersions ? 5 : 4}
                            className="animate-pulse bg-indigo-50 py-4 text-center text-indigo-600"
                          >
                            <Loader2 className="mr-2 inline h-4 w-4 animate-spin" />
                            Processing your uploads...
                          </TableCell>
                        </TableRow>
                      )}
                    </TableBody>
                  </Table>
                </div>

                {/* Pagination */}
                {totalPages > 1 && (
                  <div className="mt-4 flex items-center justify-between">
                    <p className="text-sm text-slate-500">
                      Showing {(currentPage - 1) * pageSize + 1}-
                      {Math.min(currentPage * pageSize, totalObjectCount)} of {totalObjectCount}
                    </p>
                    <div className="flex items-center space-x-2">
                      <Button
                        variant="outline"
                        size="icon"
                        disabled={currentPage <= 1}
                        onClick={() => setCurrentPage((p) => Math.max(1, p - 1))}
                        className="h-8 w-8"
                      >
                        <ChevronLeft className="h-4 w-4" />
                      </Button>
                      <span className="text-sm text-slate-600">
                        Page {currentPage} of {totalPages}
                      </span>
                      <Button
                        variant="outline"
                        size="icon"
                        disabled={currentPage >= totalPages}
                        onClick={() => setCurrentPage((p) => Math.min(totalPages, p + 1))}
                        className="h-8 w-8"
                      >
                        <ChevronRight className="h-4 w-4" />
                      </Button>
                    </div>
                  </div>
                )}
              </>
            )}
          </CardContent>
        </Card>
      </div>

      {/* Object Details Dialog */}
      <Dialog open={!!selectedObject} onOpenChange={(open) => !open && setSelectedObject(null)}>
        <DialogContent className="sm:max-w-[500px]">
          <DialogHeader>
            <DialogTitle>Object Details</DialogTitle>
            <DialogDescription className="font-mono text-xs">
              {selectedObject?.objectKey}
            </DialogDescription>
          </DialogHeader>
          {selectedObject && (
            <div className="space-y-4 py-4">
              <div className="grid grid-cols-3 gap-4 text-sm">
                <div className="text-slate-500">ETag</div>
                <div className="col-span-2 font-mono text-xs break-all">{selectedObject.eTag}</div>
                
                <div className="text-slate-500">Content-Type</div>
                <div className="col-span-2">{selectedObject.contentType}</div>
                
                <div className="text-slate-500">Version ID</div>
                <div className="col-span-2 font-mono text-xs">{selectedObject.versionId}</div>
                
                <div className="text-slate-500">Last Modified</div>
                <div className="col-span-2">{new Date(selectedObject.lastModified).toLocaleString()}</div>
                
                <div className="text-slate-500">Size</div>
                <div className="col-span-2">{(selectedObject.size / 1024).toFixed(2)} KB</div>

                <div className="text-slate-500">Encryption</div>
                <div className="col-span-2">
                  {selectedObject.encryption ? (
                    <span className="flex items-center text-green-600">
                      <ShieldCheck className="mr-1 h-3 w-3" />
                      {selectedObject.encryption}
                    </span>
                  ) : (
                    <span className="text-slate-400">None</span>
                  )}
                </div>

                <div className="text-slate-500">Object Lock</div>
                <div className="col-span-2">
                  <div className="space-y-1">
                    {selectedObject.lockStatus.legalHold && (
                      <span className="flex items-center text-orange-600 font-semibold">
                        <ShieldAlert className="mr-1 h-3 w-3" />
                        Legal Hold Active
                      </span>
                    )}
                    {selectedObject.lockStatus.retentionMode ? (
                      <span className="flex items-center text-indigo-600">
                        <History className="mr-1 h-3 w-3" />
                        {selectedObject.lockStatus.retentionMode} until {new Date(selectedObject.lockStatus.retainUntilDate!).toLocaleDateString()}
                      </span>
                    ) : !selectedObject.lockStatus.legalHold && (
                      <span className="text-slate-400">No locks active</span>
                    )}
                  </div>
                </div>
              </div>
              
              <div className="border-t pt-4">
                <Label className="mb-2 block">Custom Metadata</Label>
                {Object.keys(selectedObject.metadata).length > 0 ? (
                  <div className="space-y-1">
                    {Object.entries(selectedObject.metadata).map(([k, v]) => (
                      <div key={k} className="flex justify-between text-xs p-1.5 bg-slate-50 rounded border">
                        <span className="font-semibold">{k}</span>
                        <span className="text-slate-600">{v}</span>
                      </div>
                    ))}
                  </div>
                ) : (
                  <p className="text-xs text-slate-500 italic">No custom metadata.</p>
                )}
              </div>
            </div>
          )}
        </DialogContent>
      </Dialog>

      {/* Delete Confirmation Dialog */}
      <AlertDialog open={!!deleteConfirm} onOpenChange={(open) => !open && setDeleteConfirm(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{deleteConfirm?.title}</AlertDialogTitle>
            <AlertDialogDescription>{deleteConfirm?.description}</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={deleteConfirm?.onConfirm}
              className="bg-red-600 hover:bg-red-700"
            >
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}