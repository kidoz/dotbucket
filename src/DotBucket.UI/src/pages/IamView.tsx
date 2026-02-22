import { useEffect, useState } from 'react';
import { apiClient } from '../lib/api-client';
import type { IamUser, IamPolicy, IamGroup } from '../types/api';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { User, Key, ShieldCheck, Users, Plus, Trash2 } from 'lucide-react';

export function IamView() {
  const [users, setUsers] = useState<IamUser[]>([]);
  const [policies, setPolicies] = useState<IamPolicy[]>([]);
  const [groups, setGroups] = useState<IamGroup[]>([]);
  const [newUserName, setNewUserName] = useState('');

  useEffect(() => {
    loadData();
  }, []);

  const loadData = async () => {
    try {
      const [u, p, g] = await Promise.all([
        apiClient.getUsers(),
        apiClient.getPolicies(),
        apiClient.getGroups()
      ]);
      setUsers(u);
      setPolicies(p);
      setGroups(g);
    } catch (err) {
      console.error('Failed to load IAM data', err);
    }
  };

  const handleCreateUser = async () => {
    if (!newUserName.trim()) return;
    try {
      await apiClient.createUser(newUserName);
      setNewUserName('');
      loadData();
    } catch (err) {
      console.error('Failed to create user', err);
    }
  };

  const handleDeleteUser = async (userName: string) => {
    if (!confirm(`Delete user ${userName}?`)) return;
    try {
      await apiClient.deleteUser(userName);
      loadData();
    } catch (err) {
      console.error('Failed to delete user', err);
    }
  };

  const handleCreateAccessKey = async (userName: string) => {
    try {
      const key = await apiClient.createAccessKey(userName);
      alert(`Access Key Created!

Access Key: ${key.accessKey}
Secret Key: ${key.secretKey}

PLEASE SAVE THIS NOW. IT WILL NEVER BE SHOWN AGAIN.`);
      loadData();
    } catch (err) {
      console.error('Failed to create access key', err);
    }
  };

  return (
    <div className="space-y-6">
      <h2 className="text-2xl font-bold tracking-tight">Identity & Access Management</h2>

      <Tabs defaultValue="users" className="w-full">
        <TabsList className="grid w-full grid-cols-4 lg:w-[400px]">
          <TabsTrigger value="users"><User className="h-4 w-4 mr-2" />Users</TabsTrigger>
          <TabsTrigger value="groups"><Users className="h-4 w-4 mr-2" />Groups</TabsTrigger>
          <TabsTrigger value="policies"><ShieldCheck className="h-4 w-4 mr-2" />Policies</TabsTrigger>
          <TabsTrigger value="keys"><Key className="h-4 w-4 mr-2" />Keys</TabsTrigger>
        </TabsList>

        <TabsContent value="users" className="space-y-4 pt-4">
          <Card>
            <CardHeader>
              <CardTitle>Users</CardTitle>
              <CardDescription>Manage administrative and application users.</CardDescription>
            </CardHeader>
            <CardContent>
              <div className="flex space-x-2 mb-4">
                <Input 
                  placeholder="New user name" 
                  value={newUserName}
                  onChange={(e) => setNewUserName(e.target.value)}
                />
                <Button onClick={handleCreateUser}><Plus className="h-4 w-4 mr-2" />Add User</Button>
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>User Name</TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead>Created At</TableHead>
                    <TableHead className="text-right">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {users.map(user => (
                    <TableRow key={user.userName}>
                      <TableCell className="font-medium">{user.userName}</TableCell>
                      <TableCell>
                        <span className="bg-green-100 text-green-700 px-2 py-0.5 rounded text-xs">
                          {user.status}
                        </span>
                      </TableCell>
                      <TableCell className="text-sm text-slate-500">
                        {new Date(user.createdAt).toLocaleDateString()}
                      </TableCell>
                      <TableCell className="text-right space-x-2">
                        <Button variant="outline" size="sm" onClick={() => handleCreateAccessKey(user.userName)}>Generate Key</Button>
                        <Button variant="ghost" size="icon" onClick={() => handleDeleteUser(user.userName)} className="text-red-500">
                          <Trash2 className="h-4 w-4" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="groups" className="pt-4">
          <Card>
            <CardHeader>
              <CardTitle>Groups</CardTitle>
              <CardDescription>Organize users into groups for easier permission management.</CardDescription>
            </CardHeader>
            <CardContent>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Group Name</TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead>Created At</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {groups.map(group => (
                    <TableRow key={group.groupName}>
                      <TableCell className="font-medium">{group.groupName}</TableCell>
                      <TableCell>{group.status}</TableCell>
                      <TableCell>{new Date(group.createdAt).toLocaleDateString()}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="policies" className="pt-4">
          <Card>
            <CardHeader>
              <CardTitle>Policies</CardTitle>
              <CardDescription>Define access control rules for S3 operations.</CardDescription>
            </CardHeader>
            <CardContent>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Policy Name</TableHead>
                    <TableHead>Type</TableHead>
                    <TableHead>Created At</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {policies.map(policy => (
                    <TableRow key={policy.policyName}>
                      <TableCell className="font-medium">{policy.policyName}</TableCell>
                      <TableCell>
                        {policy.isBuiltin ? (
                          <span className="bg-blue-100 text-blue-700 px-2 py-0.5 rounded text-xs">Built-in</span>
                        ) : (
                          <span className="bg-slate-100 text-slate-700 px-2 py-0.5 rounded text-xs">Custom</span>
                        )}
                      </TableCell>
                      <TableCell className="text-sm text-slate-500">
                        {new Date(policy.createdAt).toLocaleDateString()}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="keys" className="pt-4">
          <Card>
            <CardHeader>
              <CardTitle>Access Keys</CardTitle>
              <CardDescription>Security credentials for standard S3 clients.</CardDescription>
            </CardHeader>
            <CardContent>
              <div className="text-sm text-slate-500 italic mb-4">
                Note: Access keys are listed under individual users in the Users tab.
              </div>
            </CardContent>
          </Card>
        </TabsContent>
      </Tabs>
    </div>
  );
}