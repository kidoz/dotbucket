import { useEffect, useState } from 'react';
import { apiClient } from '../lib/api-client';
import type { ClusterStatus } from '../types/api';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Globe, Loader2 } from 'lucide-react';

export function ClusterView() {
  const [cluster, setCluster] = useState<ClusterStatus | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    async function loadCluster() {
      try {
        const data = await apiClient.getClusterStatus();
        setCluster(data);
      } catch (err) {
        console.error('Failed to load cluster status', err);
      } finally {
        setLoading(false);
      }
    }
    loadCluster();
  }, []);

  if (loading) return <div className="flex justify-center p-12"><Loader2 className="animate-spin" /></div>;

  return (
    <div className="space-y-6">
      <h2 className="text-2xl font-bold tracking-tight">Cluster Management</h2>

      {!cluster?.enabled ? (
        <Card className="bg-slate-50 border-dashed">
          <CardContent className="flex flex-col items-center justify-center py-12 text-slate-500">
            <Globe className="h-12 w-12 mb-4 opacity-20" />
            <p className="text-lg font-medium">Standalone Mode</p>
            <p className="text-sm">This node is running in standalone mode. Clustering is disabled.</p>
          </CardContent>
        </Card>
      ) : (
        <>
          <div className="grid gap-4 md:grid-cols-3">
            <Card>
              <CardHeader className="pb-2">
                <CardTitle className="text-sm font-medium">Replication Factor</CardTitle>
              </CardHeader>
              <CardContent>
                <div className="text-2xl font-bold">{cluster.replicationFactor}x</div>
              </CardContent>
            </Card>
            <Card>
              <CardHeader className="pb-2">
                <CardTitle className="text-sm font-medium">Write Quorum</CardTitle>
              </CardHeader>
              <CardContent>
                <div className="text-2xl font-bold">{cluster.writeQuorum} nodes</div>
              </CardContent>
            </Card>
            <Card>
              <CardHeader className="pb-2">
                <CardTitle className="text-sm font-medium">Read Quorum</CardTitle>
              </CardHeader>
              <CardContent>
                <div className="text-2xl font-bold">{cluster.readQuorum} nodes</div>
              </CardContent>
            </Card>
          </div>

          <Card>
            <CardHeader>
              <CardTitle>Cluster Nodes</CardTitle>
              <CardDescription>Status and health of all nodes in the storage pool.</CardDescription>
            </CardHeader>
            <CardContent>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Node ID</TableHead>
                    <TableHead>Address</TableHead>
                    <TableHead>Role</TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead>Last Seen</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {cluster.nodes?.map((node) => (
                    <TableRow key={node.nodeId}>
                      <TableCell className="font-medium">
                        {node.nodeId}
                        {node.isSelf && (
                          <span className="ml-2 rounded bg-blue-100 px-1 text-[10px] text-blue-700">self</span>
                        )}
                      </TableCell>
                      <TableCell className="font-mono text-xs">{node.address}</TableCell>
                      <TableCell>
                        {node.isLeader ? (
                          <span className="rounded bg-purple-100 px-1.5 py-0.5 text-xs text-purple-700 font-semibold">Leader</span>
                        ) : (
                          <span className="text-slate-400 text-xs">Follower</span>
                        )}
                      </TableCell>
                      <TableCell>
                        <span className={`inline-flex items-center space-x-1`}>
                          <span className={`inline-block h-2 w-2 rounded-full ${
                            node.status === 'Healthy'
                              ? 'bg-green-500'
                              : node.status === 'Suspect'
                                ? 'bg-yellow-500'
                                : 'bg-red-500'
                          }`} />
                          <span className="text-sm font-medium">{node.status}</span>
                        </span>
                      </TableCell>
                      <TableCell className="text-xs text-slate-500">
                        {node.lastSeen ? new Date(node.lastSeen).toLocaleTimeString() : '-'}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        </>
      )}
    </div>
  );
}