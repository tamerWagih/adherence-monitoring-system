export default function Home() {
  return (
    <main className="flex min-h-screen flex-col items-center justify-center p-24">
      <div className="z-10 max-w-5xl w-full items-center justify-center font-mono text-sm">
        <h1 className="text-4xl font-bold mb-4">
          Adherence Monitoring System
        </h1>
        <p className="text-lg text-gray-600 mb-8">
          Admin Frontend - Coming Soon
        </p>
        <div className="bg-gray-100 p-4 rounded-lg">
          <p className="text-sm">
            Backend API: {process.env.NEXT_PUBLIC_API_URL || 'Not configured'}
          </p>
        </div>
      </div>
    </main>
  );
}

