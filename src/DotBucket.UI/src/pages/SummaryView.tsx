import { useEffect, useState, useMemo } from 'react';
import { apiClient } from '../lib/api-client';
import type { ClusterStatus } from '../types/api';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Database, Folder, HardDrive, Server, PieChart as PieChartIcon } from 'lucide-react';
import { 
  PieChart, 
  Pie, 
  Cell, 
  ResponsiveContainer, 
  Tooltip, 
  Legend
} from 'recharts';

export function SummaryView() {
  const [bucketsData, setBucketsData] = useState<{ name: string; size: number; count: number }[]>([]);
  const [cluster, setCluster] = useState<ClusterStatus | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    async function loadStats() {
      try {
        setLoading(true);
        const buckets = await apiClient.getBuckets();
        const results = await Promise.all(
          buckets.map(async (b) => {
            const res = await apiClient.getObjects(b.name);
            return {
              name: b.name,
              size: res.objects.reduce((acc, obj) => acc + obj.size, 0),
              count: res.totalCount
            };
          })
        );

        setBucketsData(results);
        const clusterStatus = await apiClient.getClusterStatus();
        setCluster(clusterStatus);
      } catch (err) {
        console.error('Failed to load summary stats', err);
      } finally {
        setLoading(false);
      }
    }
    loadStats();
  }, []);

  const stats = useMemo(() => ({
    buckets: bucketsData.length,
    objects: bucketsData.reduce((acc, b) => acc + b.count, 0),
    storage: bucketsData.reduce((acc, b) => acc + b.size, 0),
  }), [bucketsData]);

  const chartData = useMemo(() => 
    bucketsData
      .filter(b => b.size > 0)
      .map(b => ({
        name: b.name,
        value: Math.round(b.size / 1024) // in KB for better visualization
      }))
      .sort((a, b) => b.value - a.value)
      .slice(0, 5) // Top 5 buckets
  , [bucketsData]);

  const COLORS = ['#6366f1', '#8b5cf6', '#38bdf8', '#0ea5e9', '#1e1b4b'];

  const formatSize = (bytes: number) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  if (loading) {
    return <div className="flex h-64 items-center justify-center italic text-slate-400">Loading system metrics...</div>;
  }

  return (
    <div className="space-y-6">
      <h2 className="text-2xl font-bold tracking-tight">System Overview</h2>
      
      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Total Buckets</CardTitle>
            <Folder className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{stats.buckets}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Total Objects</CardTitle>
            <Database className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{stats.objects}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Storage Used</CardTitle>
            <HardDrive className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{formatSize(stats.storage)}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Active Nodes</CardTitle>
            <Server className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">
              {cluster?.enabled ? cluster.nodes?.filter(n => n.status === 'Healthy').length : 1}
            </div>
          </CardContent>
        </Card>
      </div>

      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-7">
        <Card className="col-span-4 shadow-sm">
          <CardHeader>
            <CardTitle className="flex items-center">
              <PieChartIcon className="mr-2 h-4 w-4 text-indigo-600" />
              Storage Distribution
            </CardTitle>
            <CardDescription>Top 5 buckets by size (KB)</CardDescription>
          </CardHeader>
          <CardContent>
            <div className="h-[300px] w-full">
              {chartData.length > 0 ? (
                <ResponsiveContainer width="100%" height="100%">
                  <PieChart>
                    <Pie
                      data={chartData}
                      cx="50%"
                      cy="50%"
                      innerRadius={60}
                      outerRadius={80}
                      paddingAngle={5}
                      dataKey="value"
                    >
                      {chartData.map((_, index) => (
                        <Cell key={`cell-${index}`} fill={COLORS[index % COLORS.length]} />
                      ))}
                    </Pie>
                    <Tooltip 
                      formatter={(value: any) => [`${value} KB`]}
                      contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 12px rgba(0,0,0,0.1)' }}
                    />
                    <Legend verticalAlign="bottom" height={36}/>
                  </PieChart>
                </ResponsiveContainer>
              ) : (
                <div className="flex h-full items-center justify-center text-slate-400 italic">
                  No data to display. Upload files to see distribution.
                </div>
              )}
            </div>
          </CardContent>
        </Card>

        <Card className="col-span-3 shadow-sm">
          <CardHeader>
            <CardTitle>Cluster Node Health</CardTitle>
            <CardDescription>Real-time status of storage pool</CardDescription>
          </CardHeader>
          <CardContent>
            <div className="space-y-4">
              {cluster?.enabled ? (
                cluster.nodes?.map(node => (
                  <div key={node.nodeId} className="flex items-center p-2 rounded-lg border bg-slate-50">
                    <div className={`h-2.5 w-2.5 rounded-full mr-3 ${node.status === 'Healthy' ? 'bg-green-500 shadow-[0_0_8px_rgba(34,197,94,0.6)]' : 'bg-red-500'}`} />
                    <div className="flex-1 overflow-hidden">
                      <div className="text-sm font-bold truncate">{node.nodeId} {node.isSelf && '(Self)'}</div>
                      <div className="text-[10px] text-slate-500 font-mono">{node.address}</div>
                    </div>
                    {node.isLeader && <span className="text-[10px] bg-purple-100 text-purple-700 px-1.5 py-0.5 rounded font-bold uppercase tracking-wider">Leader</span>}
                  </div>
                ))
              ) : (
                <div className="flex items-center p-3 rounded-lg border bg-slate-50">
                  <div className="h-2.5 w-2.5 rounded-full bg-green-500 mr-3 shadow-[0_0_8px_rgba(34,197,94,0.6)]" />
                  <div className="flex-1">
                    <div className="text-sm font-bold">Standalone Instance</div>
                    <div className="text-[10px] text-slate-500 font-mono">localhost</div>
                  </div>
                </div>
              )}
            </div>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}