import {
  Injectable,
  CanActivate,
  ExecutionContext,
  UnauthorizedException,
} from '@nestjs/common';
import { WorkstationAuthService } from '../adherence/services/workstation-auth.service';

/**
 * WorkstationAuthGuard
 * 
 * Validates API key and workstation ID from request headers.
 * Used for Desktop Agent authentication.
 * 
 * Required Headers:
 * - X-API-Key: 43-character API key
 * - X-Workstation-ID: UUID workstation ID
 */
@Injectable()
export class WorkstationAuthGuard implements CanActivate {
  constructor(private workstationAuthService: WorkstationAuthService) {}

  async canActivate(context: ExecutionContext): Promise<boolean> {
    const request = context.switchToHttp().getRequest();

    // Extract API key and workstation ID from headers
    const apiKey = request.headers['x-api-key'];
    const workstationId = request.headers['x-workstation-id'];

    if (!apiKey || !workstationId) {
      throw new UnauthorizedException(
        'Missing API key or workstation ID. Provide X-API-Key and X-Workstation-ID headers.',
      );
    }

    // Validate API key and workstation
    const workstation = await this.workstationAuthService.validateApiKey(
      workstationId,
      apiKey,
    );

    if (!workstation || !workstation.isActive) {
      throw new UnauthorizedException(
        'Invalid API key or inactive workstation',
      );
    }

    // Attach workstation info to request for use in controllers
    request.workstation = workstation;
    request.employeeId = workstation.employeeId;

    // Update last_seen_at timestamp
    await this.workstationAuthService.updateLastSeen(workstationId);

    return true;
  }
}

