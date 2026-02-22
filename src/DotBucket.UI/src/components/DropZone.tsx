import { useState, useRef, type DragEvent, type ChangeEvent } from 'react';
import { Upload } from 'lucide-react';

interface DropZoneProps {
  onFiles: (files: File[]) => void;
  disabled?: boolean;
}

export function DropZone({ onFiles, disabled }: DropZoneProps) {
  const [dragOver, setDragOver] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  const handleDragOver = (e: DragEvent) => {
    e.preventDefault();
    if (!disabled) setDragOver(true);
  };

  const handleDragLeave = (e: DragEvent) => {
    e.preventDefault();
    setDragOver(false);
  };

  const handleDrop = (e: DragEvent) => {
    e.preventDefault();
    setDragOver(false);
    if (disabled) return;
    const files = Array.from(e.dataTransfer.files);
    if (files.length > 0) onFiles(files);
  };

  const handleChange = (e: ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files ? Array.from(e.target.files) : [];
    if (files.length > 0) onFiles(files);
    if (inputRef.current) inputRef.current.value = '';
  };

  return (
    <div
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
      onClick={() => !disabled && inputRef.current?.click()}
      className={`flex cursor-pointer flex-col items-center justify-center rounded-lg border-2 border-dashed p-6 transition-colors ${
        dragOver
          ? 'border-indigo-500 bg-indigo-50'
          : 'border-slate-300 bg-white hover:border-slate-400 hover:bg-slate-50'
      } ${disabled ? 'cursor-not-allowed opacity-50' : ''}`}
    >
      <Upload className={`mb-2 h-8 w-8 ${dragOver ? 'text-indigo-500' : 'text-slate-400'}`} />
      <p className="text-sm text-slate-600">
        {dragOver ? 'Drop files here' : 'Drag & drop files, or click to browse'}
      </p>
      <input
        ref={inputRef}
        type="file"
        multiple
        className="hidden"
        onChange={handleChange}
        disabled={disabled}
      />
    </div>
  );
}
