export interface ValidationResult {
  valid: boolean;
  error?: string;
}

export function validateBucketName(name: string): ValidationResult {
  if (name.length < 3) {
    return { valid: false, error: 'Bucket name must be at least 3 characters.' };
  }
  if (name.length > 63) {
    return { valid: false, error: 'Bucket name must be at most 63 characters.' };
  }
  if (!/^[a-z0-9.-]+$/.test(name)) {
    return {
      valid: false,
      error: 'Bucket name can only contain lowercase letters, numbers, dots, and hyphens.',
    };
  }
  if (/^[.-]/.test(name) || /[.-]$/.test(name)) {
    return { valid: false, error: 'Bucket name cannot start or end with a dot or hyphen.' };
  }
  if (name.includes('..')) {
    return { valid: false, error: 'Bucket name cannot contain consecutive dots.' };
  }
  if (name.startsWith('xn--')) {
    return { valid: false, error: 'Bucket name cannot start with "xn--".' };
  }
  if (name.endsWith('-s3alias')) {
    return { valid: false, error: 'Bucket name cannot end with "-s3alias".' };
  }
  // No IP-format
  const parts = name.split('.');
  if (parts.length === 4 && parts.every((p) => /^\d+$/.test(p))) {
    return { valid: false, error: 'Bucket name cannot be in IP address format.' };
  }
  return { valid: true };
}
