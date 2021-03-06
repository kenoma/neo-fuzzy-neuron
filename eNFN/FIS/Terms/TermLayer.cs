﻿using System;
using System.Collections.Generic;
using System.Linq;
using eNFN.FIS.MembershipFunctions;

namespace eNFN.FIS.Terms
{
    public class TermLayer<T> where T : IMembershipFunction, new()
    {
        private const double Threshold = 1e-20;
        private readonly T _membershipFunction = new T();
        private readonly double _learningRate;
        private readonly double _smoothingAverageRate;
        private readonly int _termsLimit;
        private readonly double _competitionLooseLimit;
        private readonly List<TermCore> _cores = new List<TermCore>();
        private double _competitionMuCap = 0.99;

        private double _maxObservedValue = double.MinValue;
        private double _minObservedValue = double.MaxValue;

        
        internal TermCore[] Cores => _cores.ToArray();

        public TermLayer(TermCore[] initialStructure = null,
            double learningRate = 1e-3,
            double smoothingAverageRate = 1e-2,
            int termsLimit = 100,
            double competitionLooseLimit = 100)
        {
            _learningRate = learningRate;
            _smoothingAverageRate = smoothingAverageRate;
            _termsLimit = termsLimit;
            _competitionLooseLimit = competitionLooseLimit;

            if (initialStructure == null)
            {
                _cores.Add(new TermCore {X = double.NegativeInfinity});
                _cores.Add(new TermCore {X = double.PositiveInfinity});
            }
            else
            {
                _cores.AddRange(initialStructure);
                if (!_cores.Any(z => double.IsNegativeInfinity(z.X)))
                    _cores.Add(new TermCore {X = double.NegativeInfinity});
                if (!_cores.Any(z => double.IsPositiveInfinity(z.X)))
                    _cores.Add(new TermCore {X = double.PositiveInfinity});
                _cores.Sort((a, b) => a.X.CompareTo(b.X));
            }
        }

        public IEnumerable<(double FiringLevel, Guid TermId)> GetActivation(double inputValue)
        {
            for (var t = 0; t < _cores.Count - 1; t++)
            {
                if (_cores[t].X <= inputValue && inputValue < _cores[t + 1].X)
                {
                    var mu = _membershipFunction.Mu(inputValue, _cores[t].X, _cores[t + 1].X);

                    if (mu > Threshold)
                        yield return (mu, _cores[t].Id);

                    mu = 1.0 - mu;
                    if (mu > Threshold)
                        yield return (mu, _cores[t + 1].Id);
                    yield break;
                }
            }
        }

        public void BackpropError(double inputValue, double error)
        {
            for (var t = 0; t < _cores.Count - 1; t++)
            {
                if (_cores[t].X <= inputValue && inputValue < _cores[t + 1].X)
                {
                    var mu = _membershipFunction.Mu(inputValue, _cores[t].X, _cores[t + 1].X);
                    error *= mu;
                    if (mu > 0.5)
                    {
                        if (double.IsFinite(_cores[t].X))
                            _cores[t].X -= _learningRate * (_cores[t].X - inputValue);
                        _cores[t].AccumulatedError -= _smoothingAverageRate * (_cores[t].AccumulatedError - error);

                        _cores[t].ActivationCompetitionsFailedInARow = 0;
                        _cores[t + 1].ActivationCompetitionsFailedInARow += 1;

                        if (mu > _competitionMuCap && t > 0)
                        {
                            _cores[t - 1].ActivationCompetitionsFailedInARow += 1;
                        }
                    }
                    else if (mu < 0.5)
                    {
                        if (double.IsFinite(_cores[t + 1].X))
                            _cores[t + 1].X -= _learningRate * (_cores[t + 1].X - inputValue);

                        _cores[t + 1].AccumulatedError -=
                            _smoothingAverageRate * (_cores[t + 1].AccumulatedError - error);

                        _cores[t + 1].ActivationCompetitionsFailedInARow = 0;
                        _cores[t].ActivationCompetitionsFailedInARow += 1;
                        if (1 - mu > _competitionMuCap && t + 2 < _cores.Count)
                        {
                            _cores[t + 2].ActivationCompetitionsFailedInARow += 1;
                        }
                    }
                    else
                    {
                        if (double.IsFinite(_cores[t].X))
                            _cores[t].X -= _learningRate * (_cores[t].X - inputValue) / 2.0;
                        _cores[t].AccumulatedError -= _smoothingAverageRate * (_cores[t].AccumulatedError - error);
                        if (double.IsFinite(_cores[t + 1].X))
                            _cores[t + 1].X -= _learningRate * (_cores[t + 1].X - inputValue) / 2.0;
                        _cores[t + 1].AccumulatedError -=
                            _smoothingAverageRate * (_cores[t + 1].AccumulatedError - error);
                    }

                    break;
                }
            }
        }

        public void CreationStep(double inputValue, double generalErrorAverage, double generalErrorStd)
        {
            _maxObservedValue = Math.Max(inputValue, _maxObservedValue);
            _minObservedValue = Math.Min(inputValue, _minObservedValue);
            for (var t = 0; t < _cores.Count - 1; t++)
            {
                if (_cores[t].X <= inputValue && inputValue < _cores[t + 1].X)
                {
                    var mu = _membershipFunction.Mu(inputValue, _cores[t].X, _cores[t + 1].X);

                    if ((mu > 0.5 && (
                            _cores[t].AccumulatedError >
                            generalErrorAverage + generalErrorStd ||
                            double.IsInfinity(_cores[t + 1].X))) ||
                        (mu <= 0.5 && (
                            _cores[t + 1].AccumulatedError >
                            generalErrorAverage + generalErrorStd ||
                            double.IsInfinity(_cores[t].X))))
                    {
                        var tau = Math.Abs(_maxObservedValue - _minObservedValue) / _termsLimit;
                        
                        var left = double.IsInfinity(_cores[t].X)
                            ? (2 * inputValue - _cores[t + 1].X)
                            : _cores[t].X;
                        var right = double.IsInfinity(_cores[t + 1].X)
                            ? (2 * inputValue - _cores[t].X)
                            : _cores[t + 1].X;

                        var tau2 = Math.Min(right - inputValue, inputValue - left);

                        if ((right - left) / 2 > tau && tau2 > tau)
                        {
                            //var termValue = (left + right) / 2;
                            _cores.Add(TermCore.Create(inputValue));
                            _cores.Sort((a, b) => a.X.CompareTo(b.X));
                        }
                    }

                    break;
                }
            }
        }

        public bool TryEliminateTerm(out Guid eliminatedTerm, double generalErrorAverage, double generalErrorStd)
        {
            eliminatedTerm = Guid.Empty;

            var candidate = _cores
                .Where(z => double.IsFinite(z.X))
                .OrderByDescending(z => z.ActivationCompetitionsFailedInARow)
                .FirstOrDefault();

            if (candidate != null && candidate.ActivationCompetitionsFailedInARow > _competitionLooseLimit)
            {
                eliminatedTerm = candidate.Id;
                _cores.Remove(candidate);
                return true;
            }

            // candidate = _cores
            //     .Where(z => double.IsFinite(z.X))
            //     .OrderByDescending(z => z.AccumulatedError)
            //     .FirstOrDefault();
            //
            // if (candidate != null && candidate.AccumulatedError > generalErrorAverage + generalErrorStd)
            // {
            //     eliminatedTerm = candidate.Id;
            //     _cores.Remove(candidate);
            //     return true;
            // }

            //if (candidate.AccumulatedError < generalErrorAverage - generalErrorStd)
            //    return false;


            return false;
        }
    }
}