import {
  IsUUID,
  IsDateString,
  IsString,
  IsNumber,
  IsInt,
  IsOptional,
  Min,
  Max,
} from 'class-validator';
import { Type as TransformType } from 'class-transformer';

export class AdherenceQueryDto {
  @IsUUID()
  @IsOptional()
  employee_id?: string;

  @IsDateString()
  @IsOptional()
  start_date?: string;

  @IsDateString()
  @IsOptional()
  end_date?: string;

  @IsString()
  @IsOptional()
  department?: string;

  @IsNumber()
  @Min(0)
  @Max(100)
  @IsOptional()
  @TransformType(() => Number)
  min_adherence?: number;

  @IsNumber()
  @Min(0)
  @Max(100)
  @IsOptional()
  @TransformType(() => Number)
  max_adherence?: number;

  @IsInt()
  @Min(1)
  @IsOptional()
  @TransformType(() => Number)
  page?: number = 1;

  @IsInt()
  @Min(1)
  @Max(100)
  @IsOptional()
  @TransformType(() => Number)
  limit?: number = 50;
}

