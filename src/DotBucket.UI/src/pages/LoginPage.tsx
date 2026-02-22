import { useState, type FormEvent } from 'react';
import { useAuth } from '../contexts/AuthContext';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';

export function LoginPage() {
  const { login } = useAuth();
  const [token, setToken] = useState('');
  const [error, setError] = useState('');

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!token.trim()) {
      setError('Please enter an admin token.');
      return;
    }

    try {
      const res = await fetch('/admin/buckets', {
        headers: { Authorization: `Bearer ${token.trim()}` },
      });
      if (res.ok) {
        login(token.trim());
      } else if (res.status === 401) {
        setError('Invalid admin token.');
      } else if (res.status === 503) {
        setError('Admin authentication is not configured on the server.');
      } else {
        setError('Unable to connect to the server.');
      }
    } catch {
      setError('Unable to connect to the server.');
    }
  };

  return (
    <div className="flex min-h-screen items-center justify-center bg-slate-50">
      <Card className="w-full max-w-sm shadow-lg">
        <CardHeader className="text-center">
          <img src="/dotbucket-logo.svg" alt="DotBucket" className="mx-auto mb-2 h-12 w-12" />
          <CardTitle>DotBucket Admin</CardTitle>
          <CardDescription>Enter your admin token to continue.</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit} className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="token">Admin Token</Label>
              <Input
                id="token"
                type="password"
                placeholder="Enter admin token"
                value={token}
                onChange={(e) => {
                  setToken(e.target.value);
                  setError('');
                }}
                autoFocus
              />
            </div>
            {error && (
              <p className="text-sm text-red-600">{error}</p>
            )}
            <Button type="submit" className="w-full">
              Sign In
            </Button>
          </form>
        </CardContent>
      </Card>
    </div>
  );
}
