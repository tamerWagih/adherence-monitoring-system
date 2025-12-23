import { Injectable } from '@nestjs/common';
import { AuthGuard } from '@nestjs/passport';

/**
 * JwtAuthGuard
 * 
 * Validates JWT tokens using Passport JWT strategy.
 * Attaches authenticated user to request object.
 */
@Injectable()
export class JwtAuthGuard extends AuthGuard('jwt') {}

