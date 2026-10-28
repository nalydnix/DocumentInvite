import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FileUploader } from './FileUploader';
import { contentTypeFor, fileProblem, type UploadFunction } from './portalApi';

function file(name: string, size = 10, type = '') {
  const f = new File(['x'.repeat(Math.min(size, 10))], name, { type });
  Object.defineProperty(f, 'size', { value: size });
  return f;
}

describe('FileUploader', () => {
  it('uploads a chosen file, shows its progress and reports it when done', async () => {
    let report: (fraction: number, processing: boolean) => void = () => {};
    let finish: () => void = () => {};
    const upload: UploadFunction = (_item, _file, onProgress) =>
      new Promise((resolve) => {
        report = (fraction, processing) => onProgress({ fraction, processing });
        finish = () => resolve({ id: 'f1', fileName: 'tax.pdf', sizeBytes: 10, uploadedAt: null });
      });
    const onUploaded = vi.fn();
    render(<FileUploader itemId="i1" itemName="Tax return" allowMultiple={false} onUploaded={onUploaded} upload={upload} />);

    await userEvent.upload(screen.getByLabelText(/Choose a file/), file('tax.pdf'));

    expect(await screen.findByText('tax.pdf')).toBeInTheDocument();
    report(0.4, false);
    expect(await screen.findByRole('progressbar', { name: 'Upload of tax.pdf' })).toHaveAttribute('aria-valuenow', '40');
    expect(screen.getByText('40%')).toBeInTheDocument();

    report(1, true);
    expect(await screen.findByText('Checking...')).toBeInTheDocument();

    finish();
    expect(await screen.findByText('Uploaded')).toBeInTheDocument();
    expect(onUploaded).toHaveBeenCalledWith({ id: 'f1', fileName: 'tax.pdf', sizeBytes: 10, uploadedAt: null });
  });

  it('shows each file with its own progress when several are chosen', async () => {
    const upload: UploadFunction = vi.fn(() => new Promise<never>(() => {}));
    render(<FileUploader itemId="i1" itemName="Statements" allowMultiple onUploaded={vi.fn()} upload={upload} />);

    await userEvent.upload(screen.getByLabelText(/Choose files/), [file('a.pdf'), file('b.png')]);

    expect(await screen.findAllByRole('progressbar')).toHaveLength(2);
    expect(upload).toHaveBeenCalledTimes(2);
  });

  it('refuses unsupported, empty and oversized files without uploading them', async () => {
    const upload = vi.fn();
    render(<FileUploader itemId="i1" itemName="Statements" allowMultiple onUploaded={vi.fn()} upload={upload} />);
    const input = screen.getByLabelText(/Choose files/);

    fireEvent.change(input, {
      target: { files: [file('virus.exe'), file('empty.pdf', 0), file('huge.pdf', 26 * 1024 * 1024)] },
    });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('virus.exe: only PDF, JPG, PNG, DOCX, XLSX or CSV');
    expect(alert).toHaveTextContent('empty.pdf: the file is empty');
    expect(alert).toHaveTextContent('huge.pdf: files can be at most 25 MB');
    expect(upload).not.toHaveBeenCalled();
  });

  it('takes only the first file when the item allows one', async () => {
    const upload: UploadFunction = vi.fn(() => new Promise<never>(() => {}));
    render(<FileUploader itemId="i1" itemName="Tax" allowMultiple={false} onUploaded={vi.fn()} upload={upload} />);

    fireEvent.drop(screen.getByTestId('dropzone-i1'), { dataTransfer: { files: [file('a.pdf'), file('b.pdf')] } });

    await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));
    expect(screen.getByRole('alert')).toHaveTextContent('This item takes one file');
  });

  it('accepts dropped files and shows a failed upload with its reason', async () => {
    const upload: UploadFunction = vi.fn(() => Promise.reject(new Error('boom')));
    render(<FileUploader itemId="i1" itemName="Tax" allowMultiple onUploaded={vi.fn()} upload={upload} />);

    fireEvent.drop(screen.getByTestId('dropzone-i1'), { dataTransfer: { files: [file('a.pdf')] } });

    expect(await screen.findByText('Failed')).toBeInTheDocument();
    expect(screen.getByText('The upload did not finish. Please try again.')).toBeInTheDocument();
  });

  it('does nothing while disabled', () => {
    const upload = vi.fn();
    render(<FileUploader itemId="i1" itemName="Tax" allowMultiple disabled onUploaded={vi.fn()} upload={upload} />);

    expect(screen.getByLabelText(/Choose files/)).toBeDisabled();
    fireEvent.drop(screen.getByTestId('dropzone-i1'), { dataTransfer: { files: [file('a.pdf')] } });
    expect(upload).not.toHaveBeenCalled();
  });

  it('offers the camera on phones with an image-only input', () => {
    render(<FileUploader itemId="i1" itemName="Tax" allowMultiple onUploaded={vi.fn()} />);
    const camera = screen.getByLabelText(/Take a photo/);
    expect(camera).toHaveAttribute('capture', 'environment');
    expect(camera).toHaveAttribute('accept', 'image/jpeg,image/png');
  });
});

describe('file checks', () => {
  it('derives the content type from the extension', () => {
    expect(contentTypeFor('Scan.JPEG')).toBe('image/jpeg');
    expect(contentTypeFor('data.csv')).toBe('text/csv');
    expect(contentTypeFor('noextension')).toBeNull();
    expect(contentTypeFor('archive.zip')).toBeNull();
  });

  it('accepts a normal file', () => {
    expect(fileProblem({ name: 'a.pdf', size: 1000 })).toBeNull();
  });
});
